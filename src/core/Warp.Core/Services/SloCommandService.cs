using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>Operator commands over SLO objectives (§8.31). Registered by <c>AddWarp</c> so any dashboard host resolves it.</summary>
public interface ISloCommandService
{
    /// <summary>
    /// Insert (Id &lt;= 0, the store assigns the key) or update an objective. Returns its id, or 0 when Id &gt; 0 names no objective (deleted meanwhile:
    /// an edit never re-creates it; the dashboard route answers 404).
    /// </summary>
    /// <remarks>
    /// Persists <paramref name="definition"/> as given: it does not validate. Callers must first validate (and
    /// normalize) it with <see cref="Slo.SloValidation.TryValidate(SloDefinition, TimeSpan?, MetricKind?, MetricKind?, out string?)"/>;
    /// the dashboard upsert endpoint does, returning a 400 on failure.
    /// </remarks>
    Task<int> Upsert(SloDefinition definition, CancellationToken ct);

    /// <summary>
    /// Deletes an objective with its evaluation, its annotations and the board tiles showing it, in one <c>SaveChanges</c>
    /// under the metric board lock (it writes board tiles, so a tile added at the same moment cannot outlive it). False when
    /// no objective has that id. Throws <see cref="TimeoutException"/> when the board lock stays held past the wait (the
    /// dashboard route answers 503).
    /// </summary>
    Task<bool> Delete(int id, CancellationToken ct);

    /// <summary>Silence alerts for an objective until <paramref name="until"/> — a breaching objective becomes Acknowledged.</summary>
    Task<bool> Acknowledge(int id, DateTime until, CancellationToken ct);
}

/// <summary>
/// <see cref="ISloCommandService"/> over the user's <typeparamref name="TContext"/> (§8.31). Low-contention admin
/// actions — an optimistic load-set-save, with a structured <c>LogInformation</c> audit, mirroring
/// <c>ErrorGroupCommandService</c>. Deleting an objective also removes its one-to-one evaluation row and its annotations (#333).
/// </summary>
public sealed class SloCommandService<TContext> : ISloCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SloCommandService<TContext>> _logger;
    private readonly IWarpSqlQueries<TContext>? _sqlQueries;

    // sqlQueries: the provider's queries, for the board lock Delete takes; absent in a host without a provider (the delete
    // then runs unguarded, like the board commands).
    public SloCommandService(TContext context, TimeProvider timeProvider, ILogger<SloCommandService<TContext>> logger, IWarpSqlQueries<TContext>? sqlQueries = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _sqlQueries = sqlQueries;
    }

    /// <summary>How long a delete waits for the board lock before it gives up with a <see cref="TimeoutException"/>.</summary>
    internal TimeSpan LockTimeout { get; init; } = TransactionLock.CommandTimeout;

    public async Task<int> Upsert(SloDefinition definition, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = definition.Id > 0
            ? await _context.Set<SloDefinition>().FirstOrDefaultAsync(x => x.Id == definition.Id, ct)
            : null;

        if (definition.Id > 0 && existing is null)
        {
            return 0;
        }

        if (existing is null)
        {
            // A negative id is an insert too: store-generated, never the caller's key.
            definition.Id = 0;
            definition.CreatedAt = now;
            definition.UpdatedAt = now;
            _context.Set<SloDefinition>().Add(definition);
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("SLO objective '{Name}' ({Kind}) created", definition.Name, definition.Kind);

            return definition.Id;
        }

        existing.Name = definition.Name;
        existing.Kind = definition.Kind;
        existing.Dimension = definition.Dimension;
        existing.Application = definition.Application;
        existing.TargetValue = definition.TargetValue;
        existing.Percentile = definition.Percentile;
        existing.WindowSeconds = definition.WindowSeconds;
        existing.Enabled = definition.Enabled;

        // Metric-kind fields. The caller validated (SloValidation), which cleared every field that does not apply to
        // the kind, so copying them as they are also clears a field left over from the objective's previous kind.
        existing.TagFilter = definition.TagFilter;
        existing.SecondaryMetric = definition.SecondaryMetric;
        existing.SecondaryTagFilter = definition.SecondaryTagFilter;
        existing.RatioTag = definition.RatioTag;
        existing.GoodValues = definition.GoodValues;
        existing.BadValues = definition.BadValues;
        existing.BaselineDays = definition.BaselineDays;
        existing.Comparison = definition.Comparison;
        existing.GaugeAggregate = definition.GaugeAggregate;
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SLO objective {Id} updated", existing.Id);

        return existing.Id;
    }

    public async Task<bool> Delete(int id, CancellationToken ct)
    {
        // Under the board lock: the tile insert re-checks the objective under it, so the two serialize and a tile added
        // while the objective is deleted is either removed with it or refused.
        var deleted = await TransactionLock.RunAsync(_sqlQueries, _context, TransactionLock.MetricBoardKey, LockTimeout, (x, token) => DeleteAsync(x, id, token), ct);
        if (deleted)
        {
            _logger.LogInformation("SLO objective {Id} deleted", id);
        }

        return deleted;
    }

    public async Task<bool> Acknowledge(int id, DateTime until, CancellationToken ct)
    {
        var evaluation = await _context.Set<SloEvaluation>().FirstOrDefaultAsync(x => x.SloDefinitionId == id, ct);
        if (evaluation is null)
        {
            return false;
        }

        evaluation.AcknowledgedUntil = until;
        if (evaluation.State == SloState.Breaching)
        {
            evaluation.State = SloState.Acknowledged;
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SLO objective {Id} acknowledged until {Until:o}", id, until);

        return true;
    }

    private static async Task<bool> DeleteAsync(DbContext context, int id, CancellationToken ct)
    {
        var definition = await context.Set<SloDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (definition is null)
        {
            return false;
        }

        var evaluation = await context.Set<SloEvaluation>().FirstOrDefaultAsync(x => x.SloDefinitionId == id, ct);
        if (evaluation is not null)
        {
            context.Set<SloEvaluation>().Remove(evaluation);
        }

        // Annotations on the objective (#333) are a soft reference: they go with it, in the same SaveChanges.
        var annotations = await context.Set<MetricAnnotation>()
            .Where(x => x.Target == MetricAnnotationTarget.Slo)
            .Where(x => x.SloDefinitionId == id)
            .ToListAsync(ct);
        context.Set<MetricAnnotation>().RemoveRange(annotations);

        // So are the board tiles showing it: a board never shows a dead objective in the normal path (the board read's
        // missing flag covers a delete made by an older binary mid rolling deploy, which takes no board lock).
        var tiles = await context.Set<MetricBoardTile>()
            .Where(x => x.Kind == MetricTileKind.Slo)
            .Where(x => x.SloDefinitionId == id)
            .ToListAsync(ct);
        context.Set<MetricBoardTile>().RemoveRange(tiles);

        context.Set<SloDefinition>().Remove(definition);
        await context.SaveChangesAsync(ct);

        return true;
    }
}
