using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Adapters;

/// <summary>
/// Drains the <see cref="DbAdapterCallRecorder"/> channel and persists completed adapter calls in
/// batches. Each drained batch runs on a fresh DI scope created via <see cref="IServiceScopeFactory"/>
/// (§0.5) resolving the user's <typeparamref name="TContext"/> — adapters run in non-server processes
/// (publisher-only / dashboard-only) that have no server context, so the call log lands on the same
/// context the caller already registered. One <c>SaveChanges</c> per batch writes the
/// <see cref="AdapterCallLog"/> rows, the write-optimised <see cref="Counter"/> rows (§6.2), and any
/// lazy <see cref="AdapterDefinition.LastSeenAt"/> refresh. A failed flush is logged at Warning and the
/// batch is dropped; the caller never observes recording failures.
/// </summary>
internal sealed class AdapterCallFlusher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly DbAdapterCallRecorder _recorder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AdapterRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;
    private readonly ILogger<AdapterCallFlusher<TContext>> _logger;

    public AdapterCallFlusher(
        DbAdapterCallRecorder recorder,
        IServiceScopeFactory scopeFactory,
        AdapterRegistry registry,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        ILogger<AdapterCallFlusher<TContext>> logger)
    {
        _recorder = recorder;
        _scopeFactory = scopeFactory;
        _registry = registry;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _recorder.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var batch = new List<AdapterCallRecord>();
            while (batch.Count < AdapterFlush.BatchSize && reader.TryRead(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            await FlushBatchAsync(batch, stoppingToken);
        }
    }

    // On shutdown the base cancels stoppingToken, which breaks the ExecuteAsync drain loop and would discard
    // records still buffered in the channel — leaving Delivered deliveries with missing final attempt rows.
    // Instead: stop accepting new records (Complete → recorder-side TryWrite now fails and counts as
    // records_dropped, as designed), stop the reader loop (base.StopAsync), then drain whatever is buffered
    // on the drain budget's own token — independent of shutdown cancellation (graceful shutdown cannot
    // discard the CHANNEL tail) but bounding even an in-flight persist (a hung database cannot hang
    // shutdown). Honest scope: one batch already read out of the channel and mid-SaveChanges at the cancel
    // instant is still dropped by the persist's own cancellation guard — accepted, same lossy-diagnostics
    // stance as a full channel.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _recorder.Complete();

        // base.StopAsync signals stoppingToken and awaits ExecuteAsync, so the channel's single reader has
        // stopped before we drain from here (the bounded channel is SingleReader — no concurrent readers).
        await base.StopAsync(cancellationToken);

        await DrainRemainingAsync();
    }

    private Task DrainRemainingAsync()
        => DrainRemainingAsync(_recorder.Reader, FlushBatchAsync, AdapterFlush.ShutdownDrainBudget);

    // Extracted + internal so tests can prove the drain budget bounds a HANGING persist (a slow or
    // unreachable database at shutdown) without a real database. The budget token is independent of the
    // host's shutdown cancellation (so graceful shutdown cannot discard the buffered tail) but is passed
    // INTO the flush, not just checked between batches — an in-flight persist is cancelled when the budget
    // elapses and the remaining tail is dropped (diagnostics, not an audit trail).
    internal static async Task DrainRemainingAsync(
        ChannelReader<AdapterCallRecord> reader,
        Func<List<AdapterCallRecord>, CancellationToken, Task> flush,
        TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);

        while (!cts.IsCancellationRequested)
        {
            var batch = new List<AdapterCallRecord>();
            while (batch.Count < AdapterFlush.BatchSize && reader.TryRead(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count == 0)
            {
                break;
            }

            try
            {
                // The budget token (never the host's shutdown token) bounds the persist: graceful shutdown
                // cannot discard the tail, but a hung database gives up at the budget, not the DB timeout.
                await flush(batch, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Budget elapsed mid-persist — stop draining. The production flush swallows this itself,
                // but the guard keeps the returns-within-budget contract independent of the flush impl.
                return;
            }
        }
    }

    private async Task FlushBatchAsync(List<AdapterCallRecord> batch, CancellationToken ct)
    {
        // Each PersistBatchAsync attempt runs on its own DI scope (§0.5) resolving a fresh TContext; the
        // fallback drains a poison batch record-by-record so one bad row loses one row, not the batch.
        var scopes = new List<IServiceScope>();
        try
        {
            await PersistWithFallbackAsync(CreateContext, batch, _registry, _configuration, _timeProvider, _logger, ct);
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }

        DbContext CreateContext()
        {
            var scope = _scopeFactory.CreateScope();
            scopes.Add(scope);

            return scope.ServiceProvider.GetRequiredService<TContext>();
        }
    }

    /// <summary>
    /// Persists <paramref name="batch"/> in one <c>SaveChanges</c> via a fresh context from
    /// <paramref name="contextFactory"/>; on any non-cancellation failure it degrades to per-record
    /// persistence (fresh context each) so a single poison record — an over-long value that slipped the
    /// scope-side clamp, a constraint violation — loses only its own row/counters instead of taking the
    /// whole ≤500-record batch (counters included) down with it. Each failure logs a Warning identifying
    /// the adapter/operation. Extracted + internal so tests can drive the degrade path against a fixture
    /// context factory (§4.8) without racing the background drain loop.
    /// </summary>
    internal static async Task PersistWithFallbackAsync(
        Func<DbContext> contextFactory,
        List<AdapterCallRecord> batch,
        AdapterRegistry registry,
        WarpConfiguration configuration,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await PersistBatchAsync(contextFactory(), batch, registry, configuration, timeProvider, ct);

            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — drop the in-flight batch (diagnostics, not an audit trail).
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adapter call-log batch flush failed for {Count} record(s); degrading to per-record persistence.", batch.Count);
        }

        // SaveChanges is atomic, so the failed batch above committed nothing — re-persist every record
        // individually. The poison record(s) fail again and are dropped; the healthy siblings land.
        foreach (var record in batch)
        {
            try
            {
                await PersistBatchAsync(contextFactory(), [record], registry, configuration, timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Adapter call-log record dropped for adapter {Adapter} operation {Operation}; sibling records persisted.", record.AdapterName, record.Operation);
            }
        }
    }

    /// <summary>
    /// Persists a batch of completed calls: one <see cref="AdapterCallLog"/> row (skipped when the record
    /// is <c>SuppressLog</c> — a FailuresOnly success) + the per-outcome <see cref="Counter"/> rows per
    /// record, plus a lazy <see cref="AdapterDefinition.LastSeenAt"/> upsert per distinct adapter.
    /// Counters and the definition upsert are always written regardless of <c>SuppressLog</c>. Extracted
    /// so tests can drive persistence directly against a
    /// fixture context (§4.8) without racing the background drain loop. One <c>SaveChanges</c> per
    /// batch — the flusher owns the operation, mirroring <c>CounterAggregator</c>.
    /// </summary>
    internal static async Task<int> PersistBatchAsync(
        DbContext context,
        IReadOnlyList<AdapterCallRecord> batch,
        AdapterRegistry registry,
        WarpConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var record in batch)
        {
            // SuppressLog (FailuresOnly success) skips the call-log ROW only — counters and the
            // definition upsert below stay unconditional so success denominators are never lost.
            if (!record.SuppressLog)
            {
                var options = registry.Resolve(record.AdapterName);
                var retention = options.CallLogRetention ?? configuration.AdapterCallLogRetention;

                context.Set<AdapterCallLog>().Add(new AdapterCallLog
                {
                    AdapterName = record.AdapterName,
                    Operation = record.Operation,
                    GroupName = record.GroupName,
                    Timestamp = record.Timestamp,
                    DurationMs = record.DurationMs,
                    Attempts = record.Attempts,
                    Outcome = record.Outcome,
                    StatusCode = record.StatusCode,
                    ExceptionType = record.ExceptionType,
                    ExceptionMessage = record.ExceptionMessage,
                    RequestSummary = record.RequestSummary,
                    RequestHeaders = record.RequestHeaders,
                    ResponseHeaders = record.ResponseHeaders,
                    RequestBody = record.RequestBody,
                    ResponseBody = record.ResponseBody,
                    MachineName = record.MachineName,
                    TraceId = record.TraceId,
                    TagsJson = SerializeTags(record.Tags),
                    CorrelationId = record.CorrelationId,
                    ExpireAt = record.Timestamp.Add(retention),
                });
            }

            AddCounters(context, record);
        }

        await UpsertDefinitionsAsync(context, batch, registry, now, ct);

        await context.SaveChangesAsync(ct);

        return batch.Count;
    }

    private static void AddCounters(DbContext context, AdapterCallRecord record)
    {
        var outcome = AdapterCounterKeys.OutcomeToken(record.Outcome);

        // Adapter-level and per-operation counters cover the list + detail stats; both success and
        // failure outcomes are counted so error rates have a real denominator (SC4).
        context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Total(record.AdapterName, outcome), Value = 1 });
        context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Operation(record.AdapterName, record.Operation, outcome), Value = 1 });

        // Duration-SUM counters (ms) mirror the count counters so average latency (sum ÷ count) is
        // aggregate-backed and survives AdapterCallLog deletion. One dur counter per dimension per call
        // (not per outcome) — the denominator is the summed outcome counts above. Counter.Value is int; a
        // single call's ms comfortably fits.
        var durationMs = (int)Math.Round(record.DurationMs, MidpointRounding.AwayFromZero);
        context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Total(record.AdapterName, AdapterCounterKeys.DurationToken), Value = durationMs });
        context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Operation(record.AdapterName, record.Operation, AdapterCounterKeys.DurationToken), Value = durationMs });

        // Latency histogram: increment the ONE Total-dimension bucket whose upper bound is the smallest >=
        // the rounded ms (the read side walks these cumulatively for p90/p95/p99). Total dimension only —
        // not bucketed per-operation/per-group — to bound counter volume.
        context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Pct(record.AdapterName, AdapterCounterKeys.BucketFor(durationMs)), Value = 1 });

        // Per-group counters (successes included) give real per-group error rates (SC15). Only written
        // when the call carried a group — group-less calls behave exactly as before.
        if (record.GroupName is not null)
        {
            context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Group(record.AdapterName, record.GroupName, outcome), Value = 1 });
            context.Set<Counter>().Add(new Counter { Key = AdapterCounterKeys.Group(record.AdapterName, record.GroupName, AdapterCounterKeys.DurationToken), Value = durationMs });
        }
    }

    private static async Task UpsertDefinitionsAsync(
        DbContext context,
        IReadOnlyList<AdapterCallRecord> batch,
        AdapterRegistry registry,
        DateTime now,
        CancellationToken ct)
    {
        var names = batch
            .Select(x => x.AdapterName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Full entities (not a projection): existing rows may be updated in place (§5.3 read-for-update).
        var existing = await context.Set<AdapterDefinition>()
            .Where(x => names.Contains(x.Name))
            .ToListAsync(ct);

        var byName = existing.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var name in names)
        {
            // Non-secret config summary + group label registered at AddAdapter time; both null for ad-hoc
            // manual scopes that were never registered.
            var configSummary = registry.ResolveConfigSummary(name);
            var groupLabel = registry.ResolveGroupLabel(name);

            // Persist the per-adapter count cap so ExpirationCleanup (which runs on a server that may not
            // have registered this adapter) can enforce it without the in-memory registry.
            var retentionCount = registry.Resolve(name).CallLogRetentionCount;

            if (byName.TryGetValue(name, out var definition))
            {
                // Lazy refresh: only touch the row once LastSeenAt is stale, so a hot adapter does not
                // write the definition on every flush.
                if (definition.LastSeenAt < now.Subtract(AdapterFlush.LastSeenStaleThreshold))
                {
                    definition.LastSeenAt = now;
                }

                // Backfill/refresh the summary — the rate limiter may have created the row first (without
                // a summary), and a redeploy can change the local config.
                if (configSummary is not null && !string.Equals(definition.ConfigSummary, configSummary, StringComparison.Ordinal))
                {
                    definition.ConfigSummary = configSummary;
                }

                // Same backfill/refresh path for the group label so a rate-limiter-created row picks it up.
                if (groupLabel is not null && !string.Equals(definition.GroupLabel, groupLabel, StringComparison.Ordinal))
                {
                    definition.GroupLabel = groupLabel;
                }

                if (retentionCount is not null && definition.CallLogRetentionCount != retentionCount)
                {
                    definition.CallLogRetentionCount = retentionCount;
                }

                continue;
            }

            context.Set<AdapterDefinition>().Add(new AdapterDefinition
            {
                Name = name,
                FirstSeenAt = now,
                LastSeenAt = now,
                ConfigSummary = configSummary,
                GroupLabel = groupLabel,
                CallLogRetentionCount = retentionCount,
            });
        }
    }

    private static string? SerializeTags(IReadOnlyList<KeyValuePair<string, string>>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        return JsonSerializer.Serialize(map);
    }
}

/// <summary>
/// Non-generic tuning constants for the adapter flusher. Kept off the generic
/// <see cref="AdapterCallFlusher{TContext}"/> so a single shared value is not duplicated per closed
/// generic type (S2743).
/// </summary>
internal static class AdapterFlush
{
    /// <summary>Max records folded into a single scope + <c>SaveChanges</c>.</summary>
    internal const int BatchSize = 500;

    /// <summary>
    /// Upper bound on the shutdown drain: <see cref="AdapterCallFlusher{TContext}.StopAsync"/> persists
    /// records still buffered at shutdown on <c>CancellationToken.None</c>, stopping once this budget elapses
    /// so a slow/unreachable database cannot hang host shutdown indefinitely.
    /// </summary>
    internal static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A definition's <c>LastSeenAt</c> is refreshed only once it is older than this — no per-call write.
    /// <b>INVARIANT:</b> <c>WarpConfiguration.AdapterDefinitionOrphanGrace</c> MUST stay comfortably larger
    /// than this value. An actively-used adapter's <c>LastSeenAt</c> lags reality by up to this window
    /// between refreshes; if the orphan grace were ≤ it, <c>ExpirationCleanup</c> would delete a live
    /// adapter's definition in that band and re-insert it on the next flush. If you change one, change the other.
    /// </summary>
    internal static readonly TimeSpan LastSeenStaleThreshold = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Builds the free-form <see cref="Counter"/> keys for adapter statistics. Keys are colon-delimited
/// and namespaced under <c>adapter:</c> so <c>CounterAggregator</c> (which groups by exact key) folds
/// each dimension into its own <c>Statistic</c> row, queryable per adapter / operation / group / outcome.
/// The outcome token is always the trailing segment and is never a date, so the hourly-bucket cleanup /
/// history parsing in <c>ExpirationCleanup</c> / <c>DashboardStatsService</c> never mistakes an adapter
/// key for an hourly stat.
/// </summary>
internal static class AdapterCounterKeys
{
    public const string Prefix = "adapter";

    // Reserved trailing token for the per-dimension duration SUM (ms). Rides the same key layout + Counter→
    // Statistic aggregation as the per-outcome COUNT tokens, so average latency (sum ÷ count) survives
    // AdapterCallLog deletion — the count denominator is already aggregate-backed. Never an AdapterCallOutcome
    // token, so OutcomeCounts folds it into DurationSum, not the call Total.
    public const string DurationToken = "dur";

    // Dimension marker for the latency histogram buckets. A pct key is Total-only and has the fixed shape
    // adapter:{adapter}:pct:{upperMs} — parts.Length == 4 with this marker — so TryParse (which only knows
    // Total at length 3 and op/grp at length >= 5) never folds it into the count/error StatSet.
    public const string PctMarker = "pct";

    // Ascending latency-bucket upper bounds (ms); the trailing int.MaxValue is the "> 10000 ms" catch-all
    // overflow bucket. A single call increments the ONE bucket whose bound is the smallest >= its rounded
    // ms (see BucketFor); the read side walks these cumulatively to derive p90/p95/p99.
    public static readonly int[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, int.MaxValue];

    public static string Total(string adapter, string outcome) => $"{Prefix}:{adapter}:{outcome}";

    public static string Operation(string adapter, string operation, string outcome) => $"{Prefix}:{adapter}:op:{operation}:{outcome}";

    public static string Group(string adapter, string group, string outcome) => $"{Prefix}:{adapter}:grp:{group}:{outcome}";

    public static string Pct(string adapter, int upperMs) => $"{Prefix}:{adapter}:{PctMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}";

    // The smallest bucket upper bound that is >= the rounded duration. Buckets is ascending and its last
    // entry is int.MaxValue, so First always matches (the final entry is the "> 10000 ms" catch-all).
    public static int BucketFor(int durationMs) => Buckets.First(bound => durationMs <= bound);

    // Inverse of the builders above — kept in the SAME type so the key format and its parser can never
    // drift apart (drift silently zeroes the dashboard, which drops unparseable keys). Layout:
    //   adapter:{name}:{outcome}                    → total
    //   adapter:{name}:op:{operation}:{outcome}     → per-operation
    //   adapter:{name}:grp:{group}:{outcome}        → per-group
    // Operation / group values may themselves contain ':', so the value is everything between the
    // dimension marker and the trailing outcome token.
    public static bool TryParse(string key, out AdapterCounterKey parsed)
    {
        parsed = default;

        var parts = key.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var adapter = parts[1];

        if (parts.Length == 3)
        {
            parsed = new AdapterCounterKey(adapter, AdapterStatDimension.Total, string.Empty, parts[^1]);

            return true;
        }

        var marker = parts[2];

        // Latency histogram buckets (adapter:{name}:pct:{upperMs}) are NOT count/error rows — they are
        // read separately via TryParsePct. Reject them here so they never pollute the count/error StatSet.
        if (string.Equals(marker, PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var value = string.Join(':', parts[3..^1]);
        var outcome = parts[^1];

        if (parts.Length >= 5 && string.Equals(marker, "op", StringComparison.Ordinal))
        {
            parsed = new AdapterCounterKey(adapter, AdapterStatDimension.Operation, value, outcome);

            return true;
        }

        if (parts.Length >= 5 && string.Equals(marker, "grp", StringComparison.Ordinal))
        {
            parsed = new AdapterCounterKey(adapter, AdapterStatDimension.Group, value, outcome);

            return true;
        }

        return false;
    }

    // Parses a latency-histogram bucket key (adapter:{name}:pct:{upperMs}). Returns false for every other
    // key shape — the disjoint counterpart to TryParse, which rejects pct keys.
    public static bool TryParsePct(string key, out string adapter, out int upperMs)
    {
        adapter = string.Empty;
        upperMs = 0;

        var parts = key.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[2], PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        adapter = parts[1];

        return true;
    }

    public static string OutcomeToken(AdapterCallOutcome outcome) => outcome switch
    {
        AdapterCallOutcome.Success => "success",
        AdapterCallOutcome.Failed => "failed",
        AdapterCallOutcome.Throttled => "throttled",
        AdapterCallOutcome.CircuitOpen => "circuit_open",
        _ => "unknown",
    };
}

/// <summary>The stat dimension a parsed adapter <see cref="Counter"/> key belongs to.</summary>
internal enum AdapterStatDimension
{
    Total = 1,
    Operation = 2,
    Group = 3,
}

/// <summary>The parsed components of an adapter <see cref="Counter"/> / <see cref="Statistic"/> key.</summary>
internal readonly record struct AdapterCounterKey(string Adapter, AdapterStatDimension Dimension, string Value, string Outcome);
