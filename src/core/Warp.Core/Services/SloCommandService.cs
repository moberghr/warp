using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>Operator commands over SLO objectives (§8.31). Registered by <c>AddWarp</c> so any dashboard host resolves it.</summary>
public interface ISloCommandService
{
    /// <summary>Insert (Id == 0) or update an objective. Returns its id.</summary>
    /// <remarks>
    /// Persists <paramref name="definition"/> as given: it does not validate. Callers must first validate (and
    /// normalize) it with <see cref="Slo.SloValidation.TryValidate(SloDefinition, TimeSpan?, MetricKind?, MetricKind?, out string?)"/>;
    /// the dashboard upsert endpoint does, returning a 400 on failure.
    /// </remarks>
    Task<int> Upsert(SloDefinition definition, CancellationToken ct);

    Task<bool> Delete(int id, CancellationToken ct);

    /// <summary>Silence alerts for an objective until <paramref name="until"/> — a breaching objective becomes Acknowledged.</summary>
    Task<bool> Acknowledge(int id, DateTime until, CancellationToken ct);
}

/// <summary>
/// <see cref="ISloCommandService"/> over the user's <typeparamref name="TContext"/> (§8.31). Low-contention admin
/// actions — an optimistic load-set-save, with a structured <c>LogInformation</c> audit, mirroring
/// <c>ErrorGroupCommandService</c>. Deleting an objective also removes its one-to-one evaluation row.
/// </summary>
public sealed class SloCommandService<TContext> : ISloCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SloCommandService<TContext>> _logger;

    public SloCommandService(TContext context, TimeProvider timeProvider, ILogger<SloCommandService<TContext>> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> Upsert(SloDefinition definition, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = definition.Id > 0
            ? await _context.Set<SloDefinition>().FirstOrDefaultAsync(x => x.Id == definition.Id, ct)
            : null;

        if (existing is null)
        {
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
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SLO objective {Id} updated", existing.Id);

        return existing.Id;
    }

    public async Task<bool> Delete(int id, CancellationToken ct)
    {
        var definition = await _context.Set<SloDefinition>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (definition is null)
        {
            return false;
        }

        var evaluation = await _context.Set<SloEvaluation>().FirstOrDefaultAsync(x => x.SloDefinitionId == id, ct);
        if (evaluation is not null)
        {
            _context.Set<SloEvaluation>().Remove(evaluation);
        }

        _context.Set<SloDefinition>().Remove(definition);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SLO objective {Id} deleted", id);

        return true;
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
}
