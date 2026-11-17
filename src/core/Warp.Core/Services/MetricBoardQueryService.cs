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
    /// <summary>The stored tiles in board order (position, then id).</summary>
    Task<IReadOnlyList<MetricBoardTileModel>> GetTiles(CancellationToken ct);

    /// <summary>
    /// Every tile (up to <see cref="MetricBoardValidation.MaxTiles"/>) evaluated over <c>[rangeStart, rangeEnd)</c>: its value now, the
    /// same window one and seven days earlier, a status and a sparkline. The whole board is one batched, tier-bounded
    /// <c>metric_point</c> read (two grouped statements at most, whatever the tile count) plus the registry rows of its
    /// instruments and one read of the instrument annotations overlapping the window (#333, attached per tile in memory)
    /// — never a scan of the days between the windows. Throws
    /// <see cref="MetricQueryValidationException"/> for an empty, inverted or over-long (&gt; 90 days) range, or one too
    /// close to the ends of the calendar.
    /// </summary>
    Task<MetricBoardModel> GetBoard(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct);
}

/// <summary><see cref="IMetricBoardQueryService"/> over the user's <typeparamref name="TContext"/> (§2.14 stays-on-TContext).</summary>
public sealed class MetricBoardQueryService<TContext> : IMetricBoardQueryService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IOptions<WarpConfiguration> _options;
    private readonly TimeProvider _timeProvider;

    public MetricBoardQueryService(TContext context, IOptions<WarpConfiguration> options, TimeProvider timeProvider)
    {
        _context = context;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<MetricBoardTileModel>> GetTiles(CancellationToken ct)
    {
        var tiles = await LoadTilesAsync(ct);

        return tiles.ConvertAll(Project);
    }

    public async Task<MetricBoardModel> GetBoard(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct)
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

        var tier = MetricSeriesMath.TierForLength(to - from);
        var tiles = await LoadTilesAsync(ct);
        var specs = tiles.ToDictionary(x => x.Id, x => MetricTileSpec.TryCreate(x, out var spec) ? spec : null);
        var names = tiles
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
        var (annotations, annotationsTruncated) = await ReadAnnotationsAsync(names, from, to, ct);

        // Two statements at most: instruments only read without tags (no filter, no ratio on any of their tiles) are
        // summed per bucket alone; the rest per bucket and tag set. An unregistered instrument is not read at all.
        var tagged = tiles
            .Where(x => instruments.ContainsKey(x.Instrument))
            .Where(x => specs[x.Id]?.NeedsTags == true)
            .Select(x => x.Instrument)
            .ToHashSet(StringComparer.Ordinal);
        var untagged = instruments.Keys
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
            var known = instruments.TryGetValue(tile.Instrument, out var info);
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
        Name = x.Name,
        Position = x.Position,
        Instrument = x.Instrument,
        Application = x.Application,
        TagFilter = x.TagFilter,
        Kind = x.Kind,
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

    private Task<List<MetricBoardTile>> LoadTilesAsync(CancellationToken ct)
        => _context.Set<MetricBoardTile>()
            .AsNoTracking()
            .OrderBy(x => x.Position)
            .ThenBy(x => x.Id)
            .Take(MetricBoardValidation.MaxTiles)
            .ToListAsync(ct);
}
