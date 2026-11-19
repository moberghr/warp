using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads the metrics health board (#326): the stored tiles and the board evaluated over a window. Registered by
/// <c>AddWarp</c> itself, so a dashboard-only host serves it without <c>AddMetrics</c> or a server. Read-only.
/// </summary>
public interface IMetricBoardQueryService
{
    /// <summary>
    /// Every named board in picker order (position, then id), with its tile count. The Default board counts the tiles
    /// with no board id; when it was never created (nothing has used the boards yet) it is listed with id 0.
    /// </summary>
    Task<IReadOnlyList<MetricBoardSummaryModel>> GetBoards(CancellationToken ct);

    /// <summary>The stored tiles of every board, Default's first, each board in board order (position, then id).</summary>
    Task<IReadOnlyList<MetricBoardTileModel>> GetTiles(CancellationToken ct);

    /// <summary>
    /// The Default board's tiles (up to <see cref="MetricBoardValidation.MaxTiles"/>) evaluated over <c>[rangeStart, rangeEnd)</c>: its value now, the
    /// same window one and seven days earlier, a status and a sparkline. The whole board is one batched, tier-bounded
    /// <c>metric_point</c> read (two grouped statements at most, whatever the tile count) plus the board row, its tiles,
    /// the registry rows of its instruments and one read of the instrument annotations overlapping the window (#333,
    /// attached per tile in memory) — never a scan of the days between the windows. Throws
    /// <see cref="MetricQueryValidationException"/> for an empty, inverted or over-long (&gt; 90 days) range, or one too
    /// close to the ends of the calendar.
    /// </summary>
    Task<MetricBoardModel> GetBoard(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct);

    /// <summary>
    /// Board <paramref name="boardId"/> evaluated like <see cref="GetBoard(DateTime, DateTime, CancellationToken)"/>; the
    /// Default board's id reads the Default board. Null when no board has that id.
    /// </summary>
    Task<MetricBoardModel?> GetBoard(int boardId, DateTime rangeStart, DateTime rangeEnd, CancellationToken ct);

    /// <summary>
    /// The tiles of EVERY board evaluated together, in the same bounded read shape (the statement count does not grow
    /// with the boards either). The deploy verdict reads this when it is given no <c>board</c>, so a tile moved to a
    /// named board keeps counting. <see cref="MetricBoardModel.Board"/> is null.
    /// </summary>
    Task<MetricBoardModel> GetAllBoards(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct);
}

/// <summary><see cref="IMetricBoardQueryService"/> over the user's <typeparamref name="TContext"/> (§2.14 stays-on-TContext).</summary>
public sealed class MetricBoardQueryService<TContext> : IMetricBoardQueryService, IMetricBoardVerdictReader
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IOptions<WarpConfiguration> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ISloQueryService _objectives;

    // The objective read is optional for direct construction (tests); AddWarp registers ISloQueryService, so DI passes it.
    public MetricBoardQueryService(TContext context, IOptions<WarpConfiguration> options, TimeProvider timeProvider, ISloQueryService? objectives = null)
    {
        _context = context;
        _options = options;
        _timeProvider = timeProvider;
        _objectives = objectives ?? new SloQueryService<TContext>(context);
    }

    public async Task<IReadOnlyList<MetricBoardSummaryModel>> GetBoards(CancellationToken ct)
    {
        var boards = await _context.Set<MetricBoard>()
            .AsNoTracking()
            .OrderBy(x => x.Position)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        var counts = await _context.Set<MetricBoardTile>()
            .AsNoTracking()
            .GroupBy(x => x.BoardId)
            .Select(x =>
                new
                {
                    x.Key,
                    Count = x.Count(),
                })
            .ToListAsync(ct);
        var defaultCount = counts
            .Where(x => x.Key == null)
            .Sum(x => x.Count);
        var byBoard = counts
            .Where(x => x.Key != null)
            .ToDictionary(x => x.Key!.Value, x => x.Count);

        var models = boards.ConvertAll(x => Summarize(x, byBoard.GetValueOrDefault(x.Id) + (x.IsDefault ? defaultCount : 0)));
        if (!boards.Exists(x => x.IsDefault))
        {
            models.Insert(0, Summarize(VirtualDefault(), defaultCount));
        }

        return models;
    }

    public async Task<IReadOnlyList<MetricBoardTileModel>> GetTiles(CancellationToken ct)
    {
        var tiles = await AllTiles().ToListAsync(ct);

        return tiles.ConvertAll(Project);
    }

    public async Task<MetricBoardModel> GetBoard(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
    {
        var (from, to) = ValidateRange(rangeStart, rangeEnd);
        var board = await _context.Set<MetricBoard>()
            .AsNoTracking()
            .Where(x => x.IsDefault)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

        return await ReadBoardAsync(board ?? VirtualDefault(), from, to, ct);
    }

    public async Task<MetricBoardModel?> GetBoard(int boardId, DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
    {
        var (from, to) = ValidateRange(rangeStart, rangeEnd);
        var board = await _context.Set<MetricBoard>()
            .AsNoTracking()
            .Where(x => x.Id == boardId)
            .FirstOrDefaultAsync(ct);

        return board is null ? null : await ReadBoardAsync(board, from, to, ct);
    }

    public Task<MetricBoardModel> GetAllBoards(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
        => ReadAllBoardsAsync(rangeStart, rangeEnd, joinObjectives: true, ct);

    Task<MetricBoardModel> IMetricBoardVerdictReader.GetAllBoardsWithoutObjectives(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
        => ReadAllBoardsAsync(rangeStart, rangeEnd, joinObjectives: false, ct);

    // The board the read reports when no Default row exists yet: the legacy null-board tiles read as Default all the same.
    private static MetricBoard VirtualDefault() => new() { Name = MetricBoardValidation.DefaultBoardName, IsDefault = true };

    private static MetricBoardSummaryModel Summarize(MetricBoard x, int tileCount) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Description = x.Description,
        Position = x.Position,
        Application = x.Application,
        IsDefault = x.IsDefault,
        TileCount = tileCount,
        CreatedBy = x.CreatedBy,
    };

    private static (DateTime From, DateTime To) ValidateRange(DateTime rangeStart, DateTime rangeEnd)
    {
        var from = MetricSeriesMath.AsUtc(rangeStart);
        var to = MetricSeriesMath.AsUtc(rangeEnd);
        if (from >= to)
        {
            throw new MetricQueryValidationException("The range must have From before To.", nameof(rangeStart));
        }

        if (to - from > TimeSpan.FromDays(IMetricQueryService.MaxRangeDays))
        {
            throw new MetricQueryValidationException($"The range must not exceed {IMetricQueryService.MaxRangeDays} days.", nameof(rangeEnd));
        }

        if (from < IMetricQueryService.EarliestFrom || to > IMetricQueryService.LatestTo)
        {
            throw new MetricQueryValidationException("The range is too close to the ends of the calendar to read.", nameof(rangeStart));
        }

        return (from, to);
    }

    // Every board's tiles: Default's (no board id) first, then by board, each in board order; bounded by the caps.
    private IQueryable<MetricBoardTile> AllTiles()
        => _context.Set<MetricBoardTile>()
            .AsNoTracking()
            .OrderBy(x => x.BoardId == null ? 0 : 1)
            .ThenBy(x => x.BoardId)
            .ThenBy(x => x.Position)
            .ThenBy(x => x.Id)
            .Take(MetricBoardValidation.MaxTiles * MetricBoardValidation.MaxBoards);

    private async Task<MetricBoardModel> ReadAllBoardsAsync(DateTime rangeStart, DateTime rangeEnd, bool joinObjectives, CancellationToken ct)
    {
        var (from, to) = ValidateRange(rangeStart, rangeEnd);
        var tiles = await AllTiles().ToListAsync(ct);

        return await EvaluateAsync(null, tiles, from, to, joinObjectives, ct);
    }

    private async Task<MetricBoardModel> ReadBoardAsync(MetricBoard board, DateTime from, DateTime to, CancellationToken ct)
    {
        // The Default board also owns the tiles with no board id (every tile stored before named boards existed).
        var boardId = board.Id;
        var query = _context.Set<MetricBoardTile>().AsNoTracking();
        query = board.IsDefault
            ? query.Where(x => x.BoardId == null || x.BoardId == boardId)
            : query.Where(x => x.BoardId == boardId);
        var tiles = await query
            .OrderBy(x => x.Position)
            .ThenBy(x => x.Id)
            .Take(MetricBoardValidation.MaxTiles)
            .ToListAsync(ct);

        return await EvaluateAsync(Summarize(board, tiles.Count), tiles, from, to, joinObjectives: true, ct);
    }

    private async Task<MetricBoardModel> EvaluateAsync(MetricBoardSummaryModel? board, List<MetricBoardTile> tiles, DateTime from, DateTime to, bool joinObjectives, CancellationToken ct)
    {
        var tier = MetricSeriesMath.TierForLength(to - from);

        // Chart tiles are pictures the dashboard draws from the series API: they get their registry kind and unit (one
        // registry read for the board) and nothing else, never a place in the batched value read or the annotation read.
        // SLO tiles read no instrument at all: they are joined to ONE objective read for the whole board.
        var evaluated = tiles.FindAll(x => x.Kind is not (MetricTileKind.Chart or MetricTileKind.Slo));
        var specs = evaluated.ToDictionary(x => x.Id, x => MetricTileSpec.TryCreate(x, out var spec) ? spec : null);
        var objectives = joinObjectives ? await ReadObjectivesAsync(tiles, ct) : [];
        var names = tiles
            .Where(x => x.Kind != MetricTileKind.Slo)
            .Select(x => x.Instrument)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var evaluatedNames = evaluated
            .Select(x => x.Instrument)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var registry = names.Count == 0
            ? []
            : await _context.Set<MetricInstrument>()
                .AsNoTracking()
                .Where(x => names.Contains(x.Name))
                .Select(x =>
                    new
                    {
                        x.Name,
                        x.Kind,
                        x.Unit,
                    })
                .ToListAsync(ct);
        var instruments = registry.ToDictionary(x => x.Name, x => (x.Kind, x.Unit), StringComparer.Ordinal);

        // Annotations (#333): one more statement for the whole board, every instrument note overlapping the window,
        // matched to the tiles in memory (never a read per tile).
        var (annotations, annotationsTruncated) = await ReadAnnotationsAsync(evaluatedNames, from, to, ct);

        // Two statements at most: instruments only read without tags (no filter, no ratio on any of their tiles) are
        // summed per bucket alone; the rest per bucket and tag set. An unregistered instrument is not read at all.
        var tagged = evaluated
            .Where(x => instruments.ContainsKey(x.Instrument))
            .Where(x => specs[x.Id]?.NeedsTags == true)
            .Select(x => x.Instrument)
            .ToHashSet(StringComparer.Ordinal);
        var untagged = evaluatedNames
            .Where(instruments.ContainsKey)
            .Where(x => !tagged.Contains(x))
            .ToList();
        var rows = new List<MetricBoardRow>();
        rows.AddRange(await ReadRowsAsync(untagged, from, to, byTags: false, ct));
        rows.AddRange(await ReadRowsAsync(tagged, from, to, byTags: true, ct));
        var byInstrument = rows
            .GroupBy(x => x.Instrument, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var fineMinutes = FineMinutes;

        // #328: the newest fine bucket (and up to one flush interval before it) is still being written, while the 1d and
        // 7d baselines are complete. Cut the current window AND the shifted baselines at the same point, so a deviation
        // compares like with like instead of reading a partial bucket as a drop.
        var completeUntil = CompleteUntil(from, to, now, fineMinutes);
        var tagCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var results = new List<MetricBoardTileResult>(tiles.Count);
        foreach (var tile in tiles)
        {
            if (tile.Kind == MetricTileKind.Slo)
            {
                // Not joined (the verdict without a board reads every objective itself): unknown, not missing.
                var objective = objectives.GetValueOrDefault(tile.SloDefinitionId ?? 0);
                results.Add(new MetricBoardTileResult
                {
                    Tile = Project(tile),
                    Slo = objective,
                    Missing = joinObjectives && objective is null,
                });

                continue;
            }

            var known = instruments.TryGetValue(tile.Instrument, out var info);
            if (tile.Kind == MetricTileKind.Chart)
            {
                results.Add(new MetricBoardTileResult
                {
                    Tile = Project(tile),
                    InstrumentKind = known ? info.Kind : null,
                    Unit = known ? info.Unit : null,
                });

                continue;
            }

            var spec = specs[tile.Id];
            var applying = annotations.FindAll(x => MetricAnnotationMatch.AppliesTo(x, tile));
            var covering = applying.FindAll(x => MetricAnnotationMatch.Covers(x, tile));
            if (!known || spec is null || !MetricBoardValidation.AcceptsInstrument(tile.Kind, info.Kind))
            {
                var unread = MetricBoardMath.Status(tile, null, null, null);
                results.Add(new MetricBoardTileResult
                {
                    Tile = Project(tile),
                    InstrumentKind = known ? info.Kind : null,
                    Unit = known ? info.Unit : null,
                    Status = MetricAnnotationMatch.Acknowledge(unread, covering, to),
                    UnacknowledgedStatus = unread,
                    Annotations = applying,
                });

                continue;
            }

            var application = MetricPointQueries.NormalizeApplication(tile.Application);
            var tileRows = byInstrument.GetValueOrDefault(tile.Instrument) ?? [];
            var scoped = application is null
                ? tileRows
                : tileRows.FindAll(x => string.Equals(x.Application, application, StringComparison.Ordinal));
            var current = MetricBoardMath.WindowValue(spec, info.Kind, scoped, from, completeUntil, now, fineMinutes, tagCache);
            var dayAgo = MetricBoardMath.WindowValue(spec, info.Kind, scoped, from.AddDays(-1), completeUntil.AddDays(-1), now, fineMinutes, tagCache);
            var weekAgo = MetricBoardMath.WindowValue(spec, info.Kind, scoped, from.AddDays(-7), completeUntil.AddDays(-7), now, fineMinutes, tagCache);
            var sparkline = MetricBoardMath.Sparkline(spec, info.Kind, scoped, from, to, tier, fineMinutes, tagCache);
            var status = MetricBoardMath.Status(tile, current, dayAgo, weekAgo);

            results.Add(new MetricBoardTileResult
            {
                Tile = Project(tile),
                InstrumentKind = info.Kind,
                Unit = info.Unit,
                Current = current,
                DayAgo = dayAgo,
                WeekAgo = weekAgo,
                Status = MetricAnnotationMatch.Acknowledge(status, covering, to),
                UnacknowledgedStatus = status,
                Annotations = applying,
                Sparkline = sparkline.ConvertAll(x => new MetricBoardSparkPoint { Timestamp = x.Timestamp, Value = x.Value }),
            });
        }

        return new MetricBoardModel
        {
            Board = board,
            From = from,
            To = to,
            Resolution = tier,
            Tiles = results,
            CompleteUntil = completeUntil,
            AnnotationsTruncated = annotationsTruncated,
        };
    }

    // The exact statement the board issues for one read shape (shared range filter + grouped projection).
    internal IQueryable<MetricBoardRow> BoardRows(IReadOnlyCollection<string> instruments, DateTime from, DateTime to, bool byTags)
        => MetricPointQueries.SummedForBoard(
            MetricPointQueries.InBoardWindows(_context.Set<MetricPoint>().AsNoTracking(), instruments, from, to, FineMinutes),
            byTags);

    private static MetricBoardTileModel Project(MetricBoardTile x) => new()
    {
        Id = x.Id,
        BoardId = x.BoardId,
        Name = x.Name,
        Position = x.Position,
        Instrument = x.Instrument,
        Application = x.Application,
        TagFilter = x.TagFilter,
        Kind = x.Kind,
        ChartQuery = x.ChartQuery,
        SloDefinitionId = x.SloDefinitionId,
        RatioTag = x.RatioTag,
        GoodValues = x.GoodValues,
        BadValues = x.BadValues,
        GaugeAggregate = x.GaugeAggregate,
        StatusRule = x.StatusRule,
        Comparison = x.Comparison,
        Threshold = x.Threshold,
        BaselineDays = x.BaselineDays,
        MaxDeviation = x.MaxDeviation,
        DeviationDirection = x.DeviationDirection,
    };

    private int FineMinutes => Math.Max(1, _options.Value.FineResolutionMinutes);

    // The start of the FINE bucket holding (now - MetricFlushInterval), clamped to [from, to]: the #328 completeness rule,
    // always at fine granularity whatever the range. MetricQueryService.CompleteCutoff cuts at the start of the display
    // tier's bucket instead (hourly or daily for a long range), so the two cuts differ for ranges past the fine tier.
    private DateTime CompleteUntil(DateTime from, DateTime to, DateTime now, int fineMinutes)
    {
        var flush = _options.Value.MetricFlushInterval > TimeSpan.Zero ? _options.Value.MetricFlushInterval : TimeSpan.Zero;
        var cutoff = MetricTiers.BucketStart(MetricTier.Fine, now - flush, fineMinutes);

        return new DateTime(Math.Clamp(cutoff.Ticks, from.Ticks, to.Ticks), DateTimeKind.Utc);
    }

    // Every objective the board's SLO tiles show, from ONE objective read (whatever the number of SLO tiles; none without
    // an SLO tile). A tile whose objective is gone finds no entry and reads as missing.
    private async Task<Dictionary<int, SloObjectiveModel>> ReadObjectivesAsync(List<MetricBoardTile> tiles, CancellationToken ct)
    {
        if (!tiles.Exists(x => x.Kind == MetricTileKind.Slo))
        {
            return [];
        }

        var list = await _objectives.GetObjectives(ct);

        return list.Items.ToDictionary(x => x.Id);
    }

    private async Task<List<MetricBoardRow>> ReadRowsAsync(IReadOnlyCollection<string> instruments, DateTime from, DateTime to, bool byTags, CancellationToken ct)
    {
        if (instruments.Count == 0)
        {
            return [];
        }

        var rows = await BoardRows(instruments, from, to, byTags).ToListAsync(ct);

        return rows.ConvertAll(x => new MetricBoardRow
        {
            Instrument = x.Instrument,
            Application = x.Application,
            Tier = x.Tier,
            BucketStart = DateTime.SpecifyKind(x.BucketStart, DateTimeKind.Utc),
            Tags = x.Tags,
            Count = x.Count,
            Sum = x.Sum,
            Min = x.Min,
            Max = x.Max,
        });
    }

    // Capped at MaxPerRead (+1 to detect truncation). When the cap bites, the notes that change a verdict must survive it:
    // acknowledged noise still active at the window's end first (it is what acknowledges a status), then the other notes
    // still active at the end (open-ended or ending at or after it), then newest. Handed back newest first, the order every
    // annotation read uses.
    private async Task<(List<MetricAnnotationModel> Notes, bool Truncated)> ReadAnnotationsAsync(List<string> instruments, DateTime from, DateTime to, CancellationToken ct)
    {
        if (instruments.Count == 0)
        {
            return ([], false);
        }

        var annotations = _context.Set<MetricAnnotation>()
            .AsNoTracking()
            .Where(x => x.Target == MetricAnnotationTarget.Instrument)
            .Where(x => instruments.Contains(x.Instrument!));

        var notes = await MetricAnnotationQueries.InWindow(annotations, from, to)
            .OrderBy(x => x.AcknowledgedNoise && (x.EndsAt == null || x.EndsAt >= to) ? 0 : 1)
            .ThenBy(x => x.EndsAt == null || x.EndsAt >= to ? 0 : 1)
            .ThenByDescending(x => x.StartsAt)
            .ThenByDescending(x => x.Id)
            .Take(MetricAnnotationValidation.MaxPerRead + 1)
            .Select(MetricAnnotationQueries.Projection)
            .ToListAsync(ct);
        var truncated = notes.Count > MetricAnnotationValidation.MaxPerRead;

        var kept = notes
            .Take(MetricAnnotationValidation.MaxPerRead)
            .OrderByDescending(x => x.StartsAt)
            .ThenByDescending(x => x.Id)
            .ToList();

        return (kept, truncated);
    }
}
