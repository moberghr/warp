using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>
/// Writes the process's in-memory metric aggregates out as partial fine-tier <see cref="MetricPoint"/> rows every
/// <c>MetricFlushInterval</c>, and once more on graceful shutdown (an ungraceful exit loses at most one interval).
/// <para>
/// A plain <see cref="BackgroundService"/> rather than a locked <c>IServerTask</c>, for the same reason as
/// <c>DroppedRecordReporter</c> (§8.32): the aggregates are per process, so every process writes its own. An idle
/// aggregator means no database work at all. The write goes through a scope-resolved <see cref="IMetricPointSink"/>
/// (§0.5): the server context in a Warp server, the host's <c>TContext</c> otherwise (§2.14).
/// </para>
/// <para>
/// <b>Failure.</b> A failed point write merges the drained aggregates back (additive, so nothing is lost or counted
/// twice) and the next flush retries, bounded by <c>MaxPendingBuckets</c> (past it the oldest buckets are dropped and
/// counted). A batch the database rejects (<see cref="DbUpdateException"/>) is retried in chunks and then row by row:
/// a row refused on its own while others were accepted is a poison row, dropped and counted
/// (<c>warp.metrics.collapsed{reason=error}</c>), and a row whose sum overflowed to infinity is never sent. The instrument registry is upserted afterwards in its own save: a registry
/// failure is logged and retried on a later flush, and never fails or re-adds the points. A primary-key conflict
/// (another process inserting the same name) retries the batch row by row, so one conflicting name never keeps the
/// other instruments out; a name that conflicts on two consecutive flushes is logged at Warning. Nothing logged here
/// names a tag value (§1.2).
/// </para>
/// <para>Not reentrant: the loop and the final flush never overlap, and neither does a caller of <see cref="FlushOnceAsync"/>.</para>
/// </summary>
internal sealed class MetricFlusher : BackgroundService
{
    // Rows per retry write when a batch was rejected; a refused chunk is then retried row by row.
    private const int RetryChunkSize = 50;

    /// <summary>How long a registry row stays fresh before a flush that still sees the instrument refreshes it.</summary>
    private static readonly TimeSpan RegistryRefreshInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarpMeterCollector _collector;
    private readonly MetricAggregator _aggregator;
    private readonly MetricCardinalityGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<MetricFlusher> _logger;
    private readonly Dictionary<string, RegistryState> _registry = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastDropWarning;
    private bool _pointsCommitted;

    public MetricFlusher(
        IServiceScopeFactory scopeFactory,
        WarpMeterCollector collector,
        MetricAggregator aggregator,
        MetricCardinalityGuard guard,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        ILogger<MetricFlusher> logger)
    {
        _scopeFactory = scopeFactory;
        _collector = collector;
        _aggregator = aggregator;
        _guard = guard;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = configuration.Value.MetricFlushInterval;

        if (_interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"MetricFlushInterval must be greater than zero (was {_interval}). It is the only delay in the metric "
                + "flusher's loop, so a non-positive value would spin the flush instead of waiting.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _timeProvider, stoppingToken);

                // The point write takes CancellationToken.None here: a stop arriving while the INSERT commits would
                // otherwise surface as a cancellation after the rows were durable, the drained aggregates would be
                // merged back, and the final flush in StopAsync would write them a second time. StopAsync waits for
                // this loop to finish before it flushes, so the uncancellable write cannot delay shutdown past it.
                await FlushCoreAsync(CancellationToken.None, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never propagate: losing a metrics flush must not fault the host. The message stays type-only; the
                // exception object (its stack) goes to Debug only.
                _logger.LogError("Metric flush failed ({ExceptionType}); the next interval retries.", ex.GetType().FullName);
                _logger.LogDebug(ex, "Metric flush failure detail.");
            }
        }
    }

    /// <summary>
    /// Final flush on graceful shutdown, so an orderly stop loses nothing. An ungraceful exit still loses what
    /// accumulated since the last interval — the documented trade.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            // The shutdown token bounds the final flush, so a stuck database cannot hold the host past its shutdown
            // timeout. A cancellation landing during COMMIT cannot double count here: nothing flushes after this.
            await FlushOnceAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (_pointsCommitted)
            {
                _logger.LogWarning(
                    "Final metric flush wrote its points, but the instrument registry upsert was cancelled or failed during shutdown ({ExceptionType}); the registry is refreshed by the next flush that sees the instrument.",
                    ex.GetType().FullName);
            }
            else
            {
                _logger.LogWarning(
                    "Final metric flush did not write its points during shutdown ({ExceptionType}); {Count} pending aggregate bucket(s) were lost.",
                    ex.GetType().FullName,
                    _aggregator.PendingCount);
            }

            _logger.LogDebug(ex, "Final metric flush failure detail.");
        }
    }

    /// <summary>
    /// Reads the observable instruments, drains the aggregator and writes the points. Returns the number of points
    /// written: 0 when the aggregator was idle (no database work was done) or the write failed (the aggregates were
    /// merged back).
    /// </summary>
    internal Task<int> FlushOnceAsync(CancellationToken ct) => FlushCoreAsync(ct, ct);

    private async Task<int> FlushCoreAsync(CancellationToken pointsCt, CancellationToken ct)
    {
        _pointsCommitted = false;
        _collector.RecordObservable();

        var drained = _aggregator.Drain();
        if (drained.Count == 0)
        {
            return 0;
        }

        var writable = MapPoints(drained);
        if (writable.Count == 0)
        {
            return 0;
        }

        var written = await WritePointsAsync(writable, pointsCt);
        if (written.Count == 0)
        {
            return 0;
        }

        _pointsCommitted = true;
        await UpsertRegistryAsync(written, ct);

        return written.Count;
    }

    // Writes the batch; on a DbUpdateException retries it in chunks, then row by row inside a refused chunk, so one
    // row the database will never accept (a poison row) loses that row, not every instrument's flush forever. Returns
    // the snapshots that are durable. A failure past the first write merges back only what is not yet durable, so
    // nothing is written twice.
    private async Task<HashSet<MetricPointSnapshot>> WritePointsAsync(
        List<MetricPointSnapshot> writable,
        CancellationToken ct)
    {
        var written = new HashSet<MetricPointSnapshot>(ReferenceEqualityComparer.Instance);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<IMetricPointSink>();

            DbUpdateException rejection;
            try
            {
                await sink.WritePointsAsync(ToPoints(writable), ct);
                written.UnionWith(writable);

                return written;
            }
            catch (DbUpdateException ex)
            {
                rejection = ex;
                _logger.LogDebug(
                    "A batch of {Count} metric points was rejected ({ExceptionType}, inner {InnerType}, provider code {ProviderCode}); retrying in chunks to isolate the rows the database refuses.",
                    writable.Count,
                    ex.GetType().FullName,
                    ex.InnerException?.GetType().FullName,
                    ProviderCode(ex));
            }

            var rejected = await WriteInChunksAsync(sink, writable, written, ct);
            HandleRejected(rejected, written.Count, rejection);

            return written;
        }
        catch (Exception ex)
        {
            // The drain already took these out of the aggregator, so any failure — including resolving the sink —
            // would lose them. The re-add is additive, so the next flush writes them exactly once.
            var unwritten = writable
                .Where(x => !written.Contains(x))
                .ToList();
            MergeBack(unwritten);

            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            LogRetained(unwritten.Count, ex);

            return written;
        }
    }

    // Chunks first, so a large batch with one poison row costs a few dozen round trips rather than one per row; a
    // refused chunk is then retried row by row. Returns the rows refused on their own.
    private static async Task<List<MetricPointSnapshot>> WriteInChunksAsync(
        IMetricPointSink sink,
        List<MetricPointSnapshot> snapshots,
        HashSet<MetricPointSnapshot> written,
        CancellationToken ct)
    {
        var rejected = new List<MetricPointSnapshot>();
        foreach (var chunk in snapshots.Chunk(RetryChunkSize))
        {
            if (await TryWriteAsync(sink, chunk, ct))
            {
                written.UnionWith(chunk);

                continue;
            }

            if (chunk.Length == 1)
            {
                rejected.Add(chunk[0]);

                continue;
            }

            foreach (var snapshot in chunk)
            {
                if (await TryWriteAsync(sink, [snapshot], ct))
                {
                    written.Add(snapshot);
                }
                else
                {
                    rejected.Add(snapshot);
                }
            }
        }

        return rejected;
    }

    // Fresh entities for every retry: the instances of a refused batch may still carry state EF assigned before the
    // rollback. The sink clears its tracker on a DbUpdateException, so a refused write leaves nothing behind.
    private static async Task<bool> TryWriteAsync(IMetricPointSink sink, IReadOnlyList<MetricPointSnapshot> snapshots, CancellationToken ct)
    {
        try
        {
            await sink.WritePointsAsync(ToPoints(snapshots), ct);

            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    // A row refused on its own while other rows of the same flush were accepted is a poison row: retrying it can only
    // fail again, every interval, so it is dropped and counted (reason=error). When NOTHING was accepted the database
    // itself is the likelier cause (an outage, a lost connection), so everything is merged back for the next flush
    // instead; a write that keeps failing is then bounded by MaxPendingBuckets, not by dropping healthy data early.
    private void HandleRejected(List<MetricPointSnapshot> rejected, int writtenCount, DbUpdateException rejection)
    {
        if (rejected.Count == 0)
        {
            return;
        }

        if (writtenCount == 0)
        {
            MergeBack(rejected);
            LogRetained(rejected.Count, rejection);

            return;
        }

        foreach (var snapshot in rejected)
        {
            _guard.Report(snapshot.Series.Instrument.Name, null, MetricCardinalityGuard.ReasonError);
        }

        _logger.LogWarning(
            "{Count} metric point(s) were refused by the database on their own ({InnerType}) and were dropped; the other {Written} point(s) of the flush were written.",
            rejected.Count,
            rejection.InnerException?.GetType().FullName ?? rejection.GetType().FullName,
            writtenCount);
    }

    private void LogRetained(int count, Exception ex)
        => _logger.LogWarning(
            "Writing {Count} metric points failed ({ExceptionType}, inner {InnerType}); they were kept in memory and the next flush retries.",
            count,
            ex.GetType().FullName,
            ex.InnerException?.GetType().FullName);

    // The bounded merge-back: whatever MaxPendingBuckets refuses (the oldest buckets) is dropped and counted, with a
    // Warning at most once a minute so a long outage does not flood the log.
    private void MergeBack(IEnumerable<MetricPointSnapshot> snapshots)
    {
        var dropped = _aggregator.MergeBack(snapshots);
        if (dropped.Count == 0)
        {
            return;
        }

        foreach (var snapshot in dropped)
        {
            MetricCardinalityGuard.Count(snapshot.Series.Instrument.Name, MetricCardinalityGuard.ReasonDropped);
        }

        var now = _timeProvider.GetUtcNow();
        if (_lastDropWarning is { } last && now - last < DropWarningInterval)
        {
            return;
        }

        _lastDropWarning = now;
        _logger.LogWarning(
            "{Count} pending metric aggregate(s) were dropped, oldest bucket first: keeping them would exceed MaxPendingBuckets while point writes keep failing. Further drops are counted on warp.metrics.collapsed (reason dropped).",
            dropped.Count);
    }

    // Every text column is bounded upstream (series tags, instrument names, normalized bounds), so a row that would
    // still not fit is a bug, and a sum that overflowed to infinity cannot be stored (SQL Server's float rejects it).
    // Either is skipped and reported as an error rather than cut or retried: a cut Tags would merge two series, a cut
    // Bounds would misalign the bucket counts, and a non-finite value would fail every flush for every instrument.
    private List<MetricPointSnapshot> MapPoints(IReadOnlyList<MetricPointSnapshot> drained)
    {
        var writable = new List<MetricPointSnapshot>(drained.Count);
        foreach (var snapshot in drained)
        {
            if (MetricPointMapper.TryToPoint(snapshot, out _))
            {
                writable.Add(snapshot);

                continue;
            }

            _guard.Report(snapshot.Series.Instrument.Name, null, MetricCardinalityGuard.ReasonError);
        }

        return writable;
    }

    // The one mapping path of every write, the first and the retries alike. Only snapshots MapPoints admitted reach
    // it, and the mapping is a pure function of the snapshot, so every one maps.
    private static List<MetricPoint> ToPoints(IEnumerable<MetricPointSnapshot> writable)
        => [.. writable.Select(x => MetricPointMapper.TryToPoint(x, out var point) ? point : throw new InvalidOperationException("A writable metric snapshot no longer maps to a row."))];

    private async Task UpsertRegistryAsync(IEnumerable<MetricPointSnapshot> drained, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var due = CollectDueRows(drained, now);
        if (due.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<IMetricPointSink>();

            // Optional: a provider package registers it, and an AddWarp-only host without one keeps the race path.
            var classifier = scope.ServiceProvider.GetService<IDatabaseExceptionClassifier>();

            HashSet<string> failed;
            try
            {
                await sink.UpsertInstrumentsAsync([.. due.Select(x => x.Row)], ct);
                failed = [];
            }
            catch (DbUpdateException ex)
            {
                // Usually another process inserting the same instrument. The sink cleared its tracker, so retry row
                // by row: one conflicting name must not keep every other instrument out of the registry.
                LogRegistryRejection(ex, null, classifier);
                failed = await UpsertEachAsync(sink, due.Select(x => x.Row), classifier, ct);
            }

            foreach (var entry in due)
            {
                if (failed.Contains(entry.Row.Name))
                {
                    ReportConflict(entry.State);

                    continue;
                }

                entry.State.LastUpsertedAt = now;
                entry.State.WrittenCollapse = entry.Signature;
                entry.State.ConflictStreak = 0;
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning(
                "Metric instrument registry upsert failed ({ExceptionType}, inner {InnerType}); the points were written and it is retried on a later flush.",
                ex.GetType().FullName,
                ex.InnerException?.GetType().FullName);
        }
    }

    // Each row in its own save; returns the names whose save still conflicted. Those stay due for the next flush.
    private async Task<HashSet<string>> UpsertEachAsync(
        IMetricPointSink sink,
        IEnumerable<MetricInstrument> rows,
        IDatabaseExceptionClassifier? classifier,
        CancellationToken ct)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                await sink.UpsertInstrumentsAsync([row], ct);
            }
            catch (DbUpdateException ex)
            {
                LogRegistryRejection(ex, row.Name, classifier);
                failed.Add(row.Name);
            }
        }

        return failed;
    }

    // A unique-key violation (or any rejection when no provider classifier is registered) is the race another process
    // wins, so it stays at Debug. Anything else is a real schema or constraint problem and is logged at Warning with
    // the exception types and the provider's error code only (SqlState on PostgreSQL, the error code elsewhere): none
    // of them carries a value (§1.2). Either way the points are already written and the row stays due.
    private void LogRegistryRejection(DbUpdateException ex, string? instrument, IDatabaseExceptionClassifier? classifier)
    {
        if (classifier is not null && !classifier.IsUniqueConstraintViolation(ex))
        {
            _logger.LogWarning(
                "Metric instrument registry write was rejected by a database error other than a key race ({ExceptionType}, inner {InnerType}, provider code {ProviderCode}); the points were written and the registry write is retried on a later flush.",
                ex.GetType().FullName,
                ex.InnerException?.GetType().FullName,
                ProviderCode(ex));

            return;
        }

        _logger.LogDebug(
            "Metric instrument registry write for {Instrument} was rejected ({ExceptionType}, inner {InnerType}, provider code {ProviderCode}).",
            instrument ?? "(batch)",
            ex.GetType().FullName,
            ex.InnerException?.GetType().FullName,
            ProviderCode(ex));
    }

    private static string? ProviderCode(DbUpdateException ex)
        => ex.InnerException is DbException db
            ? db.SqlState ?? db.ErrorCode.ToString(CultureInfo.InvariantCulture)
            : null;

    // A single conflict is a race another process wins and the next flush resolves, so it stays at Debug. The same
    // name conflicting on two consecutive flushes is not a race: warn once, with the instrument name only (§1.2).
    private void ReportConflict(RegistryState state)
    {
        state.ConflictStreak++;
        if (state.ConflictStreak == 2)
        {
            _logger.LogWarning(
                "Metric instrument {Instrument} hit a registry write conflict on two consecutive flushes; its points are still written and the registry write is retried on every flush.",
                state.Info.Name);

            return;
        }

        _logger.LogDebug("Metric instrument {Instrument} registry upsert conflicted; it is retried on the next flush.", state.Info.Name);
    }

    private List<(RegistryState State, MetricInstrument Row, string? Signature)> CollectDueRows(
        IEnumerable<MetricPointSnapshot> drained,
        DateTime now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in drained.Select(x => x.Series))
        {
            var instrument = series.Instrument;
            if (IsSynthetic(instrument.Name))
            {
                continue;
            }

            if (!_registry.TryGetValue(instrument.Name, out var state))
            {
                state = new RegistryState(instrument, now);
                _registry[instrument.Name] = state;
            }

            state.LastSeenAt = now;
            state.AddTagKeys(series.Tags);
            seen.Add(instrument.Name);
        }

        var collapses = _guard.Collapsed()
            .Where(x => !IsSynthetic(x.Instrument))
            .ToDictionary(x => x.Instrument, StringComparer.Ordinal);

        var due = new List<(RegistryState State, MetricInstrument Row, string? Signature)>();
        foreach (var state in _registry.Values)
        {
            collapses.TryGetValue(state.Info.Name, out var collapse);

            var signature = collapse is null ? null : CollapseSignature(collapse);
            var collapsedChanged = !string.Equals(signature, state.WrittenCollapse, StringComparison.Ordinal);
            var stale = seen.Contains(state.Info.Name)
                && (state.LastUpsertedAt is not { } last || now - last >= RegistryRefreshInterval);

            if (stale || collapsedChanged)
            {
                due.Add((state, MetricPointMapper.ToInstrument(state.Info, state.TagKeys, state.FirstSeenAt, state.LastSeenAt, collapse), signature));
            }
        }

        return due;
    }

    // The synthetic names the cardinality guard reports under are not instruments: never a registry row.
    private static bool IsSynthetic(string name)
        => string.Equals(name, MetricSeries.Other, StringComparison.Ordinal)
            || string.Equals(name, MetricCardinalityGuard.ObservableInstrument, StringComparison.Ordinal);

    private static string CollapseSignature(MetricCollapse collapse)
        => $"{string.Join(',', collapse.Reasons)};{string.Join(',', collapse.TagKeys)}";

    private sealed class RegistryState
    {
        private readonly SortedSet<string> _tagKeys = new(StringComparer.Ordinal);

        public RegistryState(MetricInstrumentInfo info, DateTime firstSeenAt)
        {
            Info = info;
            FirstSeenAt = firstSeenAt;
            LastSeenAt = firstSeenAt;
        }

        public MetricInstrumentInfo Info { get; }

        public DateTime FirstSeenAt { get; }

        public DateTime LastSeenAt { get; set; }

        public DateTime? LastUpsertedAt { get; set; }

        /// <summary>Consecutive flushes whose registry write for this instrument conflicted.</summary>
        public int ConflictStreak { get; set; }

        /// <summary>The collapse signature last written to the registry; null while none was.</summary>
        public string? WrittenCollapse { get; set; }

        public IReadOnlyCollection<string> TagKeys => _tagKeys;

        public void AddTagKeys(string tags)
        {
            if (string.Equals(tags, MetricSeries.NoTags, StringComparison.Ordinal)
                || string.Equals(tags, MetricSeries.Other, StringComparison.Ordinal))
            {
                return;
            }

            _tagKeys.UnionWith(MetricSeriesMath.ParseTags(tags).Keys);
        }
    }
}

/// <summary>
/// Maps drained aggregates and instrument metadata to storage rows. Point rows are never truncated (see
/// <see cref="TryToPoint"/>); only free-text registry metadata (meter, unit, description, key lists) is cut to fit.
/// </summary>
internal static class MetricPointMapper
{
    public const int MaxNameLength = 128;

    public const int MaxTagsLength = MetricSeries.MaxTagsLength;

    public const int MaxBucketsLength = 512;

    public const int MaxBoundsLength = 256;

    public const int MaxTagKeysLength = 256;

    private const int MaxUnitLength = 32;

    private const int MaxDescriptionLength = 256;

    /// <summary>
    /// Maps one drained aggregate to its row. Never truncates: the key columns (instrument, application, tags) and
    /// the histogram columns (bounds, bucket counts) must be stored whole or not at all, since a cut key merges two
    /// series and a cut ladder misaligns the counts. False when any of them would not fit — a bug upstream, which
    /// bounds every one of them before a measurement is recorded — or when the sum, min or max is not finite.
    /// </summary>
    public static bool TryToPoint(MetricPointSnapshot snapshot, [NotNullWhen(true)] out MetricPoint? point)
    {
        point = null;
        var series = snapshot.Series;
        var buckets = snapshot.Buckets is null ? null : MetricBuckets.FormatCounts(snapshot.Buckets);

        // A sum of large finite measurements can reach infinity; no provider stores it (SQL Server's float rejects
        // it), so the row is unwritable rather than a reason to fail the whole batch.
        if (!double.IsFinite(snapshot.Sum) || !double.IsFinite(snapshot.Min) || !double.IsFinite(snapshot.Max))
        {
            return false;
        }

        // invariant: enforced upstream by WarpMeterCollector (name and application), MetricSeries.Resolve (tags, to
        // MetricSeries.MaxTagsLength) and MetricBuckets.TryNormalize (bounds); kept as a guard, never a cut.
        if (series.Instrument.Name.Length > MaxNameLength
            || series.Application?.Length > MaxNameLength
            || series.Tags.Length > MaxTagsLength
            || buckets?.Length > MaxBucketsLength
            || !TryBoundsCsv(snapshot.Bounds, out var bounds))
        {
            return false;
        }

        var tracksExtremes = series.Instrument.Kind is MetricKind.Histogram or MetricKind.Gauge;
        point = new MetricPoint
        {
            Instrument = series.Instrument.Name,
            Application = series.Application,
            Tags = series.Tags,
            Tier = MetricTier.Fine,
            BucketStart = snapshot.BucketStart,
            Count = snapshot.Count,
            Sum = snapshot.Sum,
            Min = tracksExtremes ? snapshot.Min : null,
            Max = tracksExtremes ? snapshot.Max : null,
            Buckets = buckets,
            Bounds = bounds,
            Compacted = false,
        };

        return true;
    }

    public static MetricInstrument ToInstrument(
        MetricInstrumentInfo info,
        IReadOnlyCollection<string> tagKeys,
        DateTime firstSeenAt,
        DateTime lastSeenAt,
        MetricCollapse? collapse)
        => new()
        {
            Name = TruncateText(info.Name, MaxNameLength),
            Meter = TruncateText(info.MeterName, MaxNameLength),
            Kind = info.Kind,
            Unit = info.Unit is null ? null : TruncateText(info.Unit, MaxUnitLength),
            Description = info.Description is null ? null : TruncateText(info.Description, MaxDescriptionLength),
            Bounds = TryBoundsCsv(info.Bounds, out var bounds) ? bounds : null,
            TagKeys = tagKeys.Count == 0 ? null : TruncateCsv(string.Join(',', tagKeys), MaxTagKeysLength),
            FirstSeenAt = firstSeenAt,
            LastSeenAt = lastSeenAt,
            CollapsedAt = collapse?.LastCollapsedAt,
            CollapsedTags = collapse is null ? null : CollapsedTagsCsv(collapse),
        };

    /// <summary>
    /// The bounds as stored (<see cref="MetricBuckets.ToCsv"/>); null when there are none. False — never a cut CSV —
    /// when they would not fit the column, which normalized bounds always do.
    /// </summary>
    public static bool TryBoundsCsv(double[]? bounds, out string? csv)
    {
        csv = bounds is null || bounds.Length == 0 ? null : MetricBuckets.ToCsv(bounds);

        return csv is null || csv.Length <= MaxBoundsLength;
    }

    /// <summary>Truncates free text to <paramref name="maxLength"/> without splitting a surrogate pair.</summary>
    public static string TruncateText(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var end = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return value[..end];
    }

    /// <summary>
    /// Truncates a CSV to <paramref name="maxLength"/> at an item boundary, so a half item never survives. A value
    /// with no boundary inside the limit falls back to <see cref="TruncateText"/>.
    /// </summary>
    public static string TruncateCsv(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = value.LastIndexOf(',', maxLength);

        return cut > 0 ? value[..cut] : TruncateText(value, maxLength);
    }

    /// <summary>The ordinal-sorted distinct union of two CSVs, truncated at an item boundary; null when both are empty.</summary>
    public static string? UnionCsv(string? first, string? second, int maxLength)
    {
        var items = new SortedSet<string>(StringComparer.Ordinal);
        AddItems(items, first);
        AddItems(items, second);

        return items.Count == 0 ? null : TruncateCsv(string.Join(',', items), maxLength);
    }

    private static void AddItems(SortedSet<string> items, string? csv)
    {
        if (string.IsNullOrEmpty(csv))
        {
            return;
        }

        foreach (var item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            items.Add(item);
        }
    }

    // Tag KEYS only (§1.2). A collapse no key is attributable to (an instrument cap, a name conflict, an error)
    // records its reasons instead, so the badge still says why.
    private static string? CollapsedTagsCsv(MetricCollapse collapse)
    {
        var items = collapse.TagKeys.Count > 0 ? collapse.TagKeys : collapse.Reasons;

        return items.Count == 0 ? null : TruncateCsv(string.Join(',', items), MaxTagKeysLength);
    }
}
