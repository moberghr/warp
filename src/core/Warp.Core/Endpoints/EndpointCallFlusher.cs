using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;

namespace Warp.Core.Endpoints;

/// <summary>
/// Drains the <see cref="DbEndpointCallRecorder"/> channel and persists completed inbound endpoint
/// requests in batches — the inbound mirror of <c>AdapterCallFlusher{TContext}</c>, but simpler: there
/// is no definition table and no registry/options dependency (the record already carries its
/// <see cref="EndpointCallRecord.ExpireAt"/>). Each drained batch runs on a fresh DI scope created via
/// <see cref="IServiceScopeFactory"/> (§0.5) resolving the user's <typeparamref name="TContext"/> —
/// endpoint observability runs in any process that hosts the middleware, which may have no server
/// context, so the call log lands on the same context the caller already registered. One
/// <c>SaveChanges</c> per batch writes the <see cref="EndpointCallLog"/> rows and the write-optimised
/// <see cref="Counter"/> rows (§6.2). A failed flush is logged at Warning and degrades to per-record
/// persistence; the caller never observes recording failures.
/// </summary>
public sealed class EndpointCallFlusher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly DbEndpointCallRecorder _recorder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EndpointCallFlusher<TContext>> _logger;

    public EndpointCallFlusher(
        DbEndpointCallRecorder recorder,
        IServiceScopeFactory scopeFactory,
        ILogger<EndpointCallFlusher<TContext>> logger)
    {
        _recorder = recorder;
        _scopeFactory = scopeFactory;
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

            var batch = new List<EndpointCallRecord>();
            while (batch.Count < EndpointFlush.BatchSize && reader.TryRead(out var record))
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

    // On shutdown the base cancels stoppingToken, which breaks the ExecuteAsync drain loop and would
    // discard records still buffered in the channel. Instead: stop accepting new records (Complete →
    // recorder-side TryWrite now fails, as designed), stop the reader loop (base.StopAsync), then drain
    // whatever is buffered on the drain budget's own token — independent of shutdown cancellation
    // (graceful shutdown cannot discard the CHANNEL tail) but bounding even an in-flight persist (a hung
    // database cannot hang shutdown).
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _recorder.Complete();

        // base.StopAsync signals stoppingToken and awaits ExecuteAsync, so the channel's single reader has
        // stopped before we drain from here (the bounded channel is SingleReader — no concurrent readers).
        await base.StopAsync(cancellationToken);

        await DrainRemainingAsync();
    }

    private Task DrainRemainingAsync()
        => DrainRemainingAsync(_recorder.Reader, FlushBatchAsync, EndpointFlush.ShutdownDrainBudget);

    // Extracted + internal so tests can prove the drain budget bounds a HANGING persist (a slow or
    // unreachable database at shutdown) without a real database. The budget token is independent of the
    // host's shutdown cancellation (so graceful shutdown cannot discard the buffered tail) but is passed
    // INTO the flush, not just checked between batches — an in-flight persist is cancelled when the budget
    // elapses and the remaining tail is dropped (diagnostics, not an audit trail).
    internal static async Task DrainRemainingAsync(
        ChannelReader<EndpointCallRecord> reader,
        Func<List<EndpointCallRecord>, CancellationToken, Task> flush,
        TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);

        while (!cts.IsCancellationRequested)
        {
            var batch = new List<EndpointCallRecord>();
            while (batch.Count < EndpointFlush.BatchSize && reader.TryRead(out var record))
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
                return;
            }
        }
    }

    private async Task FlushBatchAsync(List<EndpointCallRecord> batch, CancellationToken ct)
    {
        // Each PersistBatchAsync attempt runs on its own DI scope (§0.5) resolving a fresh TContext; the
        // fallback drains a poison batch record-by-record so one bad row loses one row, not the batch.
        var scopes = new List<IServiceScope>();
        try
        {
            await PersistWithFallbackAsync(CreateContext, batch, _logger, ct);
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }

        (DbContext Context, TimeProvider TimeProvider) CreateContext()
        {
            var scope = _scopeFactory.CreateScope();
            scopes.Add(scope);

            return (
                scope.ServiceProvider.GetRequiredService<TContext>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>());
        }
    }

    /// <summary>
    /// Persists <paramref name="batch"/> in one <c>SaveChanges</c> via a fresh context from
    /// <paramref name="contextFactory"/>; on any non-cancellation failure it degrades to per-record
    /// persistence (fresh context each) so a single poison record loses only its own row/counters instead
    /// of taking the whole ≤500-record batch down with it. Each failure logs a Warning. Extracted +
    /// internal so tests can drive the degrade path against a fixture context factory (§4.8) without
    /// racing the background drain loop.
    /// </summary>
    internal static async Task PersistWithFallbackAsync(
        Func<(DbContext Context, TimeProvider TimeProvider)> contextFactory,
        List<EndpointCallRecord> batch,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var (context, timeProvider) = contextFactory();
            await PersistBatchAsync(context, batch, timeProvider, ct);

            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — drop the in-flight batch (diagnostics, not an audit trail).
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Endpoint call-log batch flush failed for {Count} record(s); degrading to per-record persistence.", batch.Count);
        }

        // SaveChanges is atomic, so the failed batch above committed nothing — re-persist every record
        // individually. The poison record(s) fail again and are dropped; the healthy siblings land.
        foreach (var record in batch)
        {
            try
            {
                var (context, timeProvider) = contextFactory();
                await PersistBatchAsync(context, [record], timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Endpoint call-log record dropped for {Method} {Route}; sibling records persisted.", record.Method, record.RouteTemplate);
            }
        }
    }

    /// <summary>
    /// Persists a batch of completed requests: one <see cref="EndpointCallLog"/> row (skipped when the
    /// record is <c>SuppressLog</c> — a FailuresOnly success) + the per-outcome <see cref="Counter"/> rows
    /// per record. Counters are always written regardless of <c>SuppressLog</c> so success denominators
    /// are never lost. Extracted + <c>internal static</c> so tests can drive persistence directly against
    /// a fixture context (§4.8) without racing the background drain loop. One <c>SaveChanges</c> per batch
    /// — the flusher owns the operation, mirroring <c>CounterAggregator</c>.
    /// </summary>
    internal static async Task<int> PersistBatchAsync(
        DbContext context,
        IReadOnlyList<EndpointCallRecord> batch,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (batch.Count == 0)
        {
            return 0;
        }

        foreach (var record in batch)
        {
            // Normalise the identity ONCE (uppercase method, constraint-stripped template) so the stored
            // log row and the counter keys share the exact same colon-free identity — the detail page joins
            // log rows to aggregate stats on it.
            var method = record.Method.ToUpperInvariant();
            var routeTemplate = EndpointCounterKeys.NormalizeTemplate(record.RouteTemplate);

            // SuppressLog (FailuresOnly success) skips the call-log ROW only — counters below stay
            // unconditional so success denominators are never lost.
            if (!record.SuppressLog)
            {
                context.Set<EndpointCallLog>().Add(new EndpointCallLog
                {
                    Method = method,
                    RouteTemplate = routeTemplate,
                    Operation = record.Operation,
                    GroupName = record.GroupName,
                    Timestamp = record.Timestamp,
                    DurationMs = record.DurationMs,
                    Outcome = record.Outcome,
                    StatusCode = record.StatusCode,
                    RemoteIp = record.RemoteIp,
                    UserAgent = record.UserAgent,
                    User = record.User,
                    ExceptionType = record.ExceptionType,
                    ExceptionMessage = record.ExceptionMessage,
                    RequestHeaders = record.RequestHeaders,
                    ResponseHeaders = record.ResponseHeaders,
                    RequestBody = record.RequestBody,
                    ResponseBody = record.ResponseBody,
                    MachineName = record.MachineName,
                    TraceId = record.TraceId,
                    TagsJson = record.TagsJson,
                    ExpireAt = record.ExpireAt,
                });
            }

            AddCounters(context, record);
        }

        await context.SaveChangesAsync(ct);

        return batch.Count;
    }

    private static void AddCounters(DbContext context, EndpointCallRecord record)
    {
        var route = EndpointCounterKeys.NormalizeRoute(record.Method, record.RouteTemplate);
        var outcome = EndpointCounterKeys.OutcomeToken(record.Outcome);

        // The route IS the operation inbound — a single Total dimension (plus an optional Group), no
        // per-operation dimension. Both success and failure outcomes are counted so error rates have a
        // real denominator.
        context.Set<Counter>().Add(new Counter { Key = EndpointCounterKeys.Total(route, outcome), Value = 1 });

        // Duration-SUM counter (ms) mirrors the count counter so average latency (sum ÷ count) is
        // aggregate-backed and survives EndpointCallLog deletion. Counter.Value is int; a single request's
        // ms comfortably fits.
        var durationMs = (int)Math.Round(record.DurationMs, MidpointRounding.AwayFromZero);
        context.Set<Counter>().Add(new Counter { Key = EndpointCounterKeys.Total(route, EndpointCounterKeys.DurationToken), Value = durationMs });

        // Latency histogram: increment the ONE Total-dimension bucket whose upper bound is the smallest >=
        // the rounded ms (the read side walks these cumulatively for p90/p95/p99). Total dimension only —
        // not bucketed per-group — to bound counter volume.
        context.Set<Counter>().Add(new Counter { Key = EndpointCounterKeys.Pct(route, EndpointCounterKeys.BucketFor(durationMs)), Value = 1 });

        // Per-group counters (successes included) give real per-group error rates. Only written when the
        // request carried a group — group-less requests behave exactly as before.
        if (record.GroupName is not null)
        {
            context.Set<Counter>().Add(new Counter { Key = EndpointCounterKeys.Group(route, record.GroupName, outcome), Value = 1 });
            context.Set<Counter>().Add(new Counter { Key = EndpointCounterKeys.Group(route, record.GroupName, EndpointCounterKeys.DurationToken), Value = durationMs });
        }
    }
}

/// <summary>
/// Non-generic tuning constants for the endpoint flusher. Kept off the generic
/// <see cref="EndpointCallFlusher{TContext}"/> so a single shared value is not duplicated per closed
/// generic type (S2743).
/// </summary>
internal static class EndpointFlush
{
    /// <summary>Max records folded into a single scope + <c>SaveChanges</c>.</summary>
    internal const int BatchSize = 500;

    /// <summary>
    /// Upper bound on the shutdown drain: <see cref="EndpointCallFlusher{TContext}.StopAsync"/> persists
    /// records still buffered at shutdown on <c>CancellationToken.None</c>, stopping once this budget
    /// elapses so a slow/unreachable database cannot hang host shutdown indefinitely.
    /// </summary>
    internal static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(5);
}
