using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Metrics;

namespace Warp.Core.Services;

/// <summary>Edits the metrics health board's tiles (#326). Registered by <c>AddWarp</c> so any dashboard host resolves it.</summary>
public interface IMetricBoardCommandService
{
    /// <summary>
    /// Insert (Id &lt;= 0, the store assigns the key) or update a tile; returns its id, null when an insert would take the
    /// board past <see cref="MetricBoardValidation.MaxTiles"/>, or 0 when Id &gt; 0 names no tile (deleted meanwhile: an
    /// edit never re-creates it). An insert with Position 0 is appended after the last tile. Throws
    /// <see cref="TimeoutException"/> when an insert cannot take the board lock in time (the dashboard route answers 503).
    /// </summary>
    /// <remarks>
    /// Persists <paramref name="tile"/> as given: callers validate (and normalize) it first with
    /// <see cref="MetricBoardTileValidator.ValidateAsync"/>, as the dashboard upsert endpoint does.
    /// </remarks>
    Task<int?> Upsert(MetricBoardTile tile, CancellationToken ct);

    Task<bool> Delete(int id, CancellationToken ct);
}

/// <summary>
/// <see cref="IMetricBoardCommandService"/> over the user's <typeparamref name="TContext"/>: a low-contention optimistic
/// load-set-save with a structured audit log, mirroring <c>SloCommandService</c>. An insert counts and adds under the
/// transaction-scoped board lock it shares with the config seeder, so two inserts at once cannot both pass the cap.
/// </summary>
public sealed class MetricBoardCommandService<TContext> : IMetricBoardCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricBoardCommandService<TContext>> _logger;
    private readonly IWarpSqlQueries<TContext>? _sqlQueries;

    // sqlQueries: the provider's queries, for the board lock; absent in a host without a provider (the insert then runs
    // unguarded, as before).
    public MetricBoardCommandService(TContext context, TimeProvider timeProvider, ILogger<MetricBoardCommandService<TContext>> logger, IWarpSqlQueries<TContext>? sqlQueries = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _sqlQueries = sqlQueries;
    }

    /// <summary>How long an insert waits for the board lock before it gives up with a <see cref="TimeoutException"/>.</summary>
    internal TimeSpan LockTimeout { get; init; } = TransactionLock.CommandTimeout;

    public async Task<int?> Upsert(MetricBoardTile tile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tile);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = tile.Id > 0
            ? await _context.Set<MetricBoardTile>()
                .Where(x => x.Id == tile.Id)
                .FirstOrDefaultAsync(ct)
            : null;

        if (tile.Id > 0 && existing is null)
        {
            return 0;
        }

        if (existing is null)
        {
            var id = await TransactionLock.RunAsync(_sqlQueries, _context, TransactionLock.MetricBoardKey, LockTimeout, (x, token) => InsertAsync(x, tile, now, token), ct);
            if (id is not null)
            {
                _logger.LogInformation("Metric board tile '{Name}' created", tile.Name);
            }

            return id;
        }

        // The caller validated, which cleared every field that does not apply to the kind and rule, so copying them as
        // they are also clears a field left over from the tile's previous kind or rule.
        existing.Name = tile.Name;
        existing.Position = tile.Position;
        existing.Instrument = tile.Instrument;
        existing.Application = tile.Application;
        existing.TagFilter = tile.TagFilter;
        existing.Kind = tile.Kind;
        existing.RatioTag = tile.RatioTag;
        existing.GoodValues = tile.GoodValues;
        existing.BadValues = tile.BadValues;
        existing.GaugeAggregate = tile.GaugeAggregate;
        existing.StatusRule = tile.StatusRule;
        existing.Comparison = tile.Comparison;
        existing.Threshold = tile.Threshold;
        existing.BaselineDays = tile.BaselineDays;
        existing.MaxDeviation = tile.MaxDeviation;
        existing.DeviationDirection = tile.DeviationDirection;
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Metric board tile {Id} updated", existing.Id);

        return existing.Id;
    }

    public async Task<bool> Delete(int id, CancellationToken ct)
    {
        var tile = await _context.Set<MetricBoardTile>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);
        if (tile is null)
        {
            return false;
        }

        _context.Set<MetricBoardTile>().Remove(tile);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Metric board tile {Id} deleted", id);

        return true;
    }

    private static async Task<int?> InsertAsync(DbContext context, MetricBoardTile tile, DateTime now, CancellationToken ct)
    {
        if (await context.Set<MetricBoardTile>().CountAsync(ct) >= MetricBoardValidation.MaxTiles)
        {
            return null;
        }

        if (tile.Position == 0)
        {
            tile.Position = (await context.Set<MetricBoardTile>().MaxAsync(x => (int?)x.Position, ct) ?? 0) + 1;
        }

        // A negative id is an insert too: store-generated, never the caller's key.
        tile.Id = 0;
        tile.CreatedAt = now;
        tile.UpdatedAt = now;
        context.Set<MetricBoardTile>().Add(tile);
        await context.SaveChangesAsync(ct);

        return tile.Id;
    }
}
