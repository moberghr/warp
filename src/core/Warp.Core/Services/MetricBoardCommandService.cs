using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>Edits the metrics health boards and their tiles (#326, §8.34). Registered by <c>AddWarp</c> so any dashboard host resolves it.</summary>
public interface IMetricBoardCommandService
{
    /// <summary>
    /// Insert (Id &lt;= 0, the store assigns the key) or update a tile; returns its id, null when the tile's board already
    /// holds <see cref="MetricBoardValidation.MaxTiles"/> tiles (an insert, or an edit that moves the tile there) or, for a
    /// chart tile, <see cref="MetricBoardValidation.MaxChartTiles"/> charts (an insert, a move, or an edit that turns a
    /// value or ratio tile into a chart), or 0 when
    /// Id &gt; 0 names no tile or <see cref="MetricBoardTile.BoardId"/> names no board (deleted meanwhile: an edit never
    /// re-creates either). On an insert a null board id, or the Default board's id, puts the tile on the Default board
    /// (stored as null). On an edit a null board id keeps the tile on its board; the Default board's id moves it there.
    /// An insert with Position 0 is appended after the last tile of its board. Returns <see cref="ObjectiveMissing"/> for an
    /// SLO tile whose objective no longer exists (deleted after the caller validated it; the check is repeated under the
    /// board lock, which <c>SloCommandService.Delete</c> takes too). Throws <see cref="TimeoutException"/> when
    /// the write cannot take the board lock in time (the dashboard route answers 503).
    /// </summary>
    /// <remarks>
    /// Persists <paramref name="tile"/> as given: callers validate (and normalize) it first with
    /// <see cref="MetricBoardTileValidator"/>, as the dashboard upsert endpoint does.
    /// </remarks>
    Task<int?> Upsert(MetricBoardTile tile, CancellationToken ct);

    /// <summary>
    /// What <see cref="Upsert"/> returns for an SLO tile whose objective does not exist (never a tile id, which is
    /// positive). The dashboard route answers 400 with <c>MetricBoardTileValidator.ObjectiveNotFound</c>.
    /// </summary>
    public const int ObjectiveMissing = -1;

    Task<bool> Delete(int id, CancellationToken ct);

    /// <summary>
    /// The Default board's id, creating the board (named <see cref="MetricBoardValidation.DefaultBoardName"/>) on first
    /// use under the board lock, so N callers at once create one. Throws <see cref="TimeoutException"/> when the lock
    /// stays held past the wait.
    /// </summary>
    Task<int> EnsureDefaultBoard(CancellationToken ct);

    /// <summary>
    /// Creates (Id &lt;= 0) or renames/edits a board. The name is unique ignoring case; at most
    /// <see cref="MetricBoardValidation.MaxBoards"/> boards. <paramref name="actor"/> (the dashboard user) is recorded as
    /// the creator of a new board and named in the audit log. Callers validate first with
    /// <see cref="MetricBoardValidation.TryValidateBoard"/>. Throws <see cref="TimeoutException"/> when the lock stays
    /// held past the wait.
    /// </summary>
    Task<MetricBoardWriteResult> UpsertBoard(MetricBoard board, string actor, CancellationToken ct);

    /// <summary>
    /// Deletes a board and every tile on it, in one <c>SaveChanges</c>. The Default board cannot be deleted. Throws
    /// <see cref="TimeoutException"/> when the lock stays held past the wait.
    /// </summary>
    Task<MetricBoardDeleteStatus> DeleteBoard(int id, string actor, CancellationToken ct);
}

/// <summary>
/// <see cref="IMetricBoardCommandService"/> over the user's <typeparamref name="TContext"/>: load-set-save with a
/// structured audit log, mirroring <c>SloCommandService</c>. Every write that can break a cap or a board reference runs
/// under the transaction-scoped board lock it shares with the config seeder: two inserts at once cannot both pass a cap,
/// a tile cannot land on a board being deleted, and two creators cannot both create the Default board.
/// </summary>
public sealed class MetricBoardCommandService<TContext> : IMetricBoardCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricBoardCommandService<TContext>> _logger;
    private readonly IWarpSqlQueries<TContext>? _sqlQueries;

    // sqlQueries: the provider's queries, for the board lock; absent in a host without a provider (the writes then run
    // unguarded, as before).
    public MetricBoardCommandService(TContext context, TimeProvider timeProvider, ILogger<MetricBoardCommandService<TContext>> logger, IWarpSqlQueries<TContext>? sqlQueries = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _sqlQueries = sqlQueries;
    }

    /// <summary>How long a write waits for the board lock before it gives up with a <see cref="TimeoutException"/>.</summary>
    internal TimeSpan LockTimeout { get; init; } = TransactionLock.CommandTimeout;

    public async Task<int?> Upsert(MetricBoardTile tile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tile);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var insert = tile.Id <= 0;
        var id = await LockedAsync((x, token) => UpsertTileAsync(x, tile, now, token), ct);
        if (id > 0 && insert)
        {
            _logger.LogInformation("Metric board tile {Id} created", id);
        }

        if (id > 0 && !insert)
        {
            _logger.LogInformation("Metric board tile {Id} updated", id);
        }

        return id;
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

    public async Task<int> EnsureDefaultBoard(CancellationToken ct)
    {
        // The common case reads the row and takes no lock; only the first use ever creates it.
        var existing = await _context.Set<MetricBoard>()
            .AsNoTracking()
            .Where(x => x.IsDefault)
            .OrderBy(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing.Value;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var board = await LockedAsync((x, token) => MetricBoardStore.EnsureDefaultAsync(x, now, token), ct);

        return board.Id;
    }

    public async Task<MetricBoardWriteResult> UpsertBoard(MetricBoard board, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(board);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var insert = board.Id <= 0;
        var result = await LockedAsync((x, token) => UpsertBoardAsync(x, board, actor, now, token), ct);
        if (result.Status == MetricBoardWriteStatus.Saved && insert)
        {
            _logger.LogInformation("Metric board {Id} created by {Actor}", result.Id, actor);
        }

        if (result.Status == MetricBoardWriteStatus.Saved && !insert)
        {
            _logger.LogInformation("Metric board {Id} updated by {Actor}", result.Id, actor);
        }

        return result;
    }

    public async Task<MetricBoardDeleteStatus> DeleteBoard(int id, string actor, CancellationToken ct)
    {
        var (status, tiles) = await LockedAsync((x, token) => DeleteBoardAsync(x, id, token), ct);
        if (status == MetricBoardDeleteStatus.Deleted)
        {
            _logger.LogInformation("Metric board {Id} deleted by {Actor} with {Tiles} tile(s)", id, actor, tiles);
        }

        return status;
    }

    private static async Task<int?> UpsertTileAsync(DbContext context, MetricBoardTile tile, DateTime now, CancellationToken ct)
    {
        var existing = tile.Id > 0
            ? await context.Set<MetricBoardTile>()
                .Where(x => x.Id == tile.Id)
                .FirstOrDefaultAsync(ct)
            : null;
        if (tile.Id > 0 && existing is null)
        {
            return 0;
        }

        // The caller's validator saw the objective before the lock; SloCommandService.Delete removes it (and its tiles)
        // under this same lock, so checking again here is what keeps a tile from outliving its objective.
        if (tile.Kind == MetricTileKind.Slo && !await context.Set<SloDefinition>().AnyAsync(x => x.Id == tile.SloDefinitionId, ct))
        {
            return IMetricBoardCommandService.ObjectiveMissing;
        }

        // An edit that names no board keeps the tile where it is (an omitted boardId is not a move to Default: the dashboard
        // sends the Default board's real id for that); on an insert no board means the Default board. The target board
        // must exist; the Default board's tiles are stored with no board id, its one representation.
        var requested = existing is not null && tile.BoardId is null ? existing.BoardId : tile.BoardId;
        var boardId = requested > 0 ? requested : null;
        if (boardId is not null)
        {
            var target = await context.Set<MetricBoard>()
                .AsNoTracking()
                .Where(x => x.Id == boardId)
                .Select(x => (bool?)x.IsDefault)
                .FirstOrDefaultAsync(ct);
            if (target is null)
            {
                return 0;
            }

            boardId = target.Value ? null : boardId;
        }

        var moving = existing is null || existing.BoardId != boardId;
        if (moving && await context.Set<MetricBoardTile>().Where(x => x.BoardId == boardId).CountAsync(ct) >= MetricBoardValidation.MaxTiles)
        {
            return null;
        }

        // A chart arriving on the board (an insert, a move, or a value/ratio tile turned into a chart) must fit the chart
        // cap too: each chart is its own series request every refresh.
        var newChart = tile.Kind == MetricTileKind.Chart && (moving || existing!.Kind != MetricTileKind.Chart);
        if (newChart && await context.Set<MetricBoardTile>()
            .Where(x => x.BoardId == boardId)
            .Where(x => x.Kind == MetricTileKind.Chart)
            .CountAsync(ct) >= MetricBoardValidation.MaxChartTiles)
        {
            return null;
        }

        if (existing is null)
        {
            if (tile.Position == 0)
            {
                tile.Position = (await context.Set<MetricBoardTile>().Where(x => x.BoardId == boardId).MaxAsync(x => (int?)x.Position, ct) ?? 0) + 1;
            }

            // A negative id is an insert too: store-generated, never the caller's key.
            tile.Id = 0;
            tile.BoardId = boardId;
            tile.CreatedAt = now;
            tile.UpdatedAt = now;
            context.Set<MetricBoardTile>().Add(tile);
            await context.SaveChangesAsync(ct);

            return tile.Id;
        }

        // The caller validated, which cleared every field that does not apply to the kind and rule, so copying them as
        // they are also clears a field left over from the tile's previous kind or rule.
        existing.BoardId = boardId;
        existing.Name = tile.Name;
        existing.Position = tile.Position;
        existing.Instrument = tile.Instrument;
        existing.Application = tile.Application;
        existing.TagFilter = tile.TagFilter;
        existing.Kind = tile.Kind;
        existing.ChartQuery = tile.ChartQuery;
        existing.SloDefinitionId = tile.SloDefinitionId;
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
        await context.SaveChangesAsync(ct);

        return existing.Id;
    }

    private static async Task<MetricBoardWriteResult> UpsertBoardAsync(DbContext context, MetricBoard board, string actor, DateTime now, CancellationToken ct)
    {
        // A new board implies the Default board exists, so the picker always lists it first.
        if (board.Id <= 0)
        {
            await MetricBoardStore.EnsureDefaultAsync(context, now, ct);
        }

        // At most MaxBoards rows; the name check ignores case in memory, never by collation (SQL Server and PostgreSQL disagree).
        var boards = await context.Set<MetricBoard>().ToListAsync(ct);
        if (boards.Exists(x => x.Id != board.Id && MetricBoardStore.SameName(x.Name, board.Name)))
        {
            return new MetricBoardWriteResult { Status = MetricBoardWriteStatus.NameTaken };
        }

        if (board.Id > 0)
        {
            var existing = boards.Find(x => x.Id == board.Id);
            if (existing is null)
            {
                return new MetricBoardWriteResult { Status = MetricBoardWriteStatus.NotFound };
            }

            existing.Name = board.Name;
            existing.Description = board.Description;
            existing.Application = board.Application;
            existing.Position = board.Position > 0 ? board.Position : existing.Position;
            existing.UpdatedAt = now;
            await context.SaveChangesAsync(ct);

            return new MetricBoardWriteResult { Status = MetricBoardWriteStatus.Saved, Id = existing.Id };
        }

        if (boards.Count >= MetricBoardValidation.MaxBoards)
        {
            return new MetricBoardWriteResult { Status = MetricBoardWriteStatus.Full };
        }

        var created = new MetricBoard
        {
            Name = board.Name,
            Description = board.Description,
            Application = board.Application,
            Position = board.Position > 0 ? board.Position : boards.Max(x => x.Position) + 1,
            IsDefault = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actor,
        };
        context.Set<MetricBoard>().Add(created);
        await context.SaveChangesAsync(ct);

        return new MetricBoardWriteResult { Status = MetricBoardWriteStatus.Saved, Id = created.Id };
    }

    private static async Task<(MetricBoardDeleteStatus Status, int Tiles)> DeleteBoardAsync(DbContext context, int id, CancellationToken ct)
    {
        var board = await context.Set<MetricBoard>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);
        if (board is null)
        {
            return (MetricBoardDeleteStatus.NotFound, 0);
        }

        if (board.IsDefault)
        {
            return (MetricBoardDeleteStatus.DefaultBoard, 0);
        }

        // The board and its tiles go in one SaveChanges (one implicit transaction): a soft reference, no cascade to lean on.
        var tiles = await context.Set<MetricBoardTile>()
            .Where(x => x.BoardId == id)
            .ToListAsync(ct);
        context.Set<MetricBoardTile>().RemoveRange(tiles);
        context.Set<MetricBoard>().Remove(board);
        await context.SaveChangesAsync(ct);

        return (MetricBoardDeleteStatus.Deleted, tiles.Count);
    }

    private Task<T> LockedAsync<T>(Func<DbContext, CancellationToken, Task<T>> work, CancellationToken ct)
        => TransactionLock.RunAsync(_sqlQueries, _context, TransactionLock.MetricBoardKey, LockTimeout, work, ct);
}
