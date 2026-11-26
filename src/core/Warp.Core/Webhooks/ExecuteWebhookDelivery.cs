using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.NoRestart;
using Warp.Core.Notifiers;

namespace Warp.Core.Webhooks;

/// <summary>
/// The executor job for one webhook delivery. It is an ordinary source-generated-style job (worker hot
/// path untouched, §0.2/§6.1): the first attempt is enqueued by <see cref="IWebhookDispatcher.SendAsync"/>
/// and each retry is a fresh copy of this job in <c>State.Scheduled</c> — the job <em>is</em> the clock,
/// there are no timers or scans. Carries only the delivery id; every field needed to execute lives on the
/// self-contained <see cref="WebhookDelivery"/> row.
/// <para>
/// Marked <see cref="RestartAttribute">[Restart]</see> so a stale (crashed-mid-execution) executor is
/// always re-run regardless of the host's <c>RestartStaleJobsByDefault</c>: the at-least-once
/// exhausted-callback recovery (and the general delivery-completes guarantee) depends on the crashed job
/// being restarted, so it must not be left to a global toggle.
/// </para>
/// </summary>
[Restart]
public sealed class ExecuteWebhookDelivery : IJob
{
    public Guid DeliveryId { get; set; }
}

/// <summary>
/// Runs one webhook attempt and advances the delivery state machine. The delivery — not the job — is the
/// state machine: this handler <b>always completes</b>. Every exception raised while attempting the HTTP
/// leg is caught and recorded as an attempt failure, and the whole persistence block is guarded so even a
/// DB fault completes the job — webhook failures never pollute the Jobs UI. On failure with retries left it
/// schedules the next executor job at <c>NextAttemptAt</c> (which rides <c>ScheduledJobActivation</c>, §2.8);
/// on exhaustion it flips the row to <c>Exhausted</c> (setting <c>ExhaustedCallbackPending</c> in the same
/// commit) and — <b>after</b> the transition commits — invokes the host's
/// <see cref="IWebhookDeliveryExhaustedHandler"/> (guarded; a throwing callback is logged, never
/// propagated), then clears the flag in a second small commit. The callback fires post-commit so it is never
/// re-fired by a rollback; it is genuinely at-least-once on process-crash edges — a crash between the
/// Exhausted commit and the callback leaves <c>ExhaustedCallbackPending</c> set, and the re-run recovers it
/// by re-invoking the (idempotent) callback and clearing the flag.
/// </summary>
internal sealed class ExecuteWebhookDeliveryHandler<TContext> : IJobHandler<ExecuteWebhookDelivery>
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IPublisher _publisher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly StandardWebhooksSigner _standardSigner;
    private readonly IEnumerable<IWebhookDeliveryExhaustedHandler> _exhaustedHandlers;
    private readonly IEnumerable<IWebhookSigner> _customSigners;
    private readonly IWarpAdapters _adapters;
    private readonly AdapterRegistry _adapterRegistry;
    private readonly WarpNotifierDispatcher _notifier;
    private readonly IEnumerable<IWebhookEndpointHealthHandler> _endpointHealthHandlers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _probeInterval;
    private readonly ILogger<ExecuteWebhookDeliveryHandler<TContext>> _logger;

    public ExecuteWebhookDeliveryHandler(
        TContext context,
        IPublisher publisher,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        StandardWebhooksSigner standardSigner,
        IEnumerable<IWebhookDeliveryExhaustedHandler> exhaustedHandlers,
        IEnumerable<IWebhookSigner> customSigners,
        IWarpAdapters adapters,
        AdapterRegistry adapterRegistry,
        WarpNotifierDispatcher notifier,
        IEnumerable<IWebhookEndpointHealthHandler> endpointHealthHandlers,
        IServiceScopeFactory scopeFactory,
        IOptions<WarpConfiguration> configuration,
        ILogger<ExecuteWebhookDeliveryHandler<TContext>> logger)
    {
        _context = context;
        _publisher = publisher;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _standardSigner = standardSigner;
        _exhaustedHandlers = exhaustedHandlers;
        _customSigners = customSigners;
        _adapters = adapters;
        _adapterRegistry = adapterRegistry;
        _notifier = notifier;
        _endpointHealthHandlers = endpointHealthHandlers;
        _scopeFactory = scopeFactory;
        _attemptTimeout = configuration.Value.WebhookAttemptTimeout;
        _probeInterval = configuration.Value.WebhookEndpointProbeInterval;
        _logger = logger;
    }

    public async Task HandleAsync(ExecuteWebhookDelivery message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The executor job ALWAYS completes: the delivery — not the job — is the state machine, so the WHOLE
        // body (including the initial delivery read) is guarded. A transient DB fault on the read must not
        // surface a Failed job in the Jobs UI, and with a host-level AddRetry it must not re-run uncoordinated
        // (BUG-2). Any fault is logged and the job completes.
        try
        {
            await ExecuteAsync(message, cancellationToken);
        }
#pragma warning disable CA1031 // executor jobs must ALWAYS complete: any fault is logged, never a failed job.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Webhook delivery {DeliveryId} execution faulted; completing the executor job.", message.DeliveryId);
        }
    }

    private async Task ExecuteAsync(ExecuteWebhookDelivery message, CancellationToken cancellationToken)
    {
        var delivery = await _context.Set<WebhookDelivery>()
            .Where(x => x.Id == message.DeliveryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            return;
        }

        // Crash-recovery for the exhausted callback (at-least-once): a row whose ExhaustedCallbackPending
        // flag is still set means the process died between the Exhausted commit and the callback. The check
        // is deliberately status-independent — a Redeliver may have flipped the row back to Pending before
        // this recovery ran, and the flip must not drop the prior exhaustion's notification. Re-invoke the
        // (idempotent) callback and clear the flag; an Exhausted row is settled (never a second HTTP
        // attempt), while a Pending row (post-Redeliver) falls through to its attempt below.
        if (delivery.ExhaustedCallbackPending)
        {
            // On a still-Exhausted row the live AttemptCount is the exhaustion's count. On a Pending row a
            // Redeliver has already reset AttemptCount to 0 (D1) — reconstruct the count the exhaustion had
            // from the immutable schedule instead: exhaustion always lands at schedule.Count + 1 attempts.
            var exhaustedAttemptCount = delivery.Status == WebhookDeliveryStatus.Exhausted
                ? delivery.AttemptCount
                : delivery.RetrySchedule.Count + 1;

            await InvokeExhaustedHandlersAsync(delivery, exhaustedAttemptCount, cancellationToken);
            delivery.ExhaustedCallbackPending = false;
            await _publisher.SaveChangesAsync(cancellationToken);

            if (delivery.Status == WebhookDeliveryStatus.Exhausted)
            {
                return;
            }
        }

        // Status guard against a concurrent double-attempt: a delivery that is not Pending has already
        // settled (Delivered/Exhausted) or is being carried by another job. Complete without a second hit.
        if (delivery.Status != WebhookDeliveryStatus.Pending)
        {
            return;
        }

        var attemptNumber = delivery.AttemptCount + 1;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // The time this attempt will schedule its retry at on failure. Computed up front so the claim can
        // stamp it together with AttemptCount — keeping the two columns consistent to any observer (§8.15
        // dashboard reads). A FINAL attempt stamps the claim time instead of null: if the outcome commit
        // below faults, the row is left Pending with no live executor job, and the stuck-delivery sweep
        // (StaleJobRecovery) finds it by its past NextAttemptAt — a null stamp would strand it forever.
        // The outcome commit nulls it again on Delivered/Exhausted, so the marker is only ever visible
        // mid-attempt or on a stuck row.
        var hasRetry = attemptNumber <= delivery.RetrySchedule.Count;
        DateTime? nextAttemptAt = hasRetry ? now + delivery.RetrySchedule[attemptNumber - 1] : now;

        // Claim this attempt atomically BEFORE the HTTP leg (BUG-3): two executor jobs for one delivery (a
        // stale-lease re-enqueue) both read the same (Pending, AttemptCount) and would each POST and race
        // AttemptCount. The guarded increment lets exactly one win — the loser matches zero rows and completes
        // quietly with no second POST. AttemptCount AND NextAttemptAt are stamped together so they stay
        // consistent; the persistence block below only syncs the tracked entity, it does not increment again.
        var claimed = await TryClaimAttemptAsync(_context, delivery.Id, delivery.AttemptCount, nextAttemptAt, cancellationToken);
        if (!claimed)
        {
            return;
        }

        var attempt = await TryDeliverAsync(delivery, cancellationToken);

        WarpTelemetry.WebhookAttempts.Add(1, new KeyValuePair<string, object?>("outcome", attempt.Succeeded ? "success" : "failed"));

        // Endpoint health (8.1) follows the flag stamped at SendAsync, never this server's own config. The row an
        // exhausting delivery flips to Failing must exist before the outcome transaction: inserting it there and
        // losing a race to another executor would abort that transaction on PostgreSQL.
        var groupName = delivery.TrackEndpointHealth ? delivery.GroupName : null;
        if (groupName is not null && !attempt.Succeeded && !hasRetry)
        {
            await EnsureEndpointAsync(groupName, cancellationToken);
        }

        var exhausted = false;
        PendingEndpointHealthSignal? healthSignal = null;

        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var attemptedAt = _timeProvider.GetUtcNow().UtcDateTime;

            // Stamps the last-attempt fields (#357) and, through the Pending guard, locks the row against a
            // concurrent Cancel: a delivery cancelled while this attempt was in flight stays Cancelled — no retry
            // is scheduled and no outcome is written over it.
            var stillPending = await _context.Set<WebhookDelivery>()
                .Where(x => x.Id == delivery.Id)
                .Where(x => x.Status == WebhookDeliveryStatus.Pending)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.LastAttemptAt, attemptedAt)
                        .SetProperty(x => x.LastStatusCode, attempt.StatusCode)
                        .SetProperty(x => x.LastFailureReason, attempt.FailureReason),
                    cancellationToken);

            if (stillPending == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogInformation("Webhook delivery {DeliveryId} was cancelled during attempt {Attempt}; its outcome is not recorded.", delivery.Id, attemptNumber);

                return;
            }

            // Sync the tracked entity with the values already claimed atomically above (the DB row already
            // carries them) so the transition below writes a consistent row; no second increment.
            delivery.AttemptCount = attemptNumber;
            delivery.NextAttemptAt = nextAttemptAt;

            if (attempt.Succeeded)
            {
                delivery.Status = WebhookDeliveryStatus.Delivered;
                delivery.NextAttemptAt = null;
                WarpTelemetry.WebhookDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", "delivered"));

                if (groupName is not null)
                {
                    healthSignal = await RecordEndpointSuccessAsync(groupName, attemptedAt, cancellationToken);
                }
            }
            else if (hasRetry)
            {
                // Retries left: the schedule column is never mutated — (RetrySchedule, AttemptCount) fully
                // determines the remaining plan. Attempt N's failure schedules delay schedule[N-1]. NextAttemptAt
                // is already stamped by the claim; the scheduled job reuses the exact same value.
                await _publisher.Schedule(new ExecuteWebhookDelivery { DeliveryId = delivery.Id }, nextAttemptAt!.Value, WebhookDefaults.Queue);

                if (groupName is not null)
                {
                    await RecordEndpointFailureAsync(groupName, attemptedAt, cancellationToken);
                }
            }
            else
            {
                // Out of retries. With endpoint health tracking the delivery parks, to be probed or released when
                // the endpoint recovers — including the one that turns a healthy endpoint Failing (the Failing signal
                // is the alert, not the exhausted callback). Without tracking, or with no endpoint row to release
                // it, the delivery is Exhausted as before.
                var outcome = groupName is null
                    ? EndpointOutcome.NotTracked
                    : await MarkEndpointFailedAsync(groupName, attemptedAt, cancellationToken);

                delivery.NextAttemptAt = null;

                if (outcome is EndpointOutcome.StartedFailing or EndpointOutcome.AlreadyFailing)
                {
                    delivery.Status = WebhookDeliveryStatus.Parked;
                    WarpTelemetry.WebhookDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", "parked"));

                    if (outcome == EndpointOutcome.StartedFailing)
                    {
                        healthSignal = new PendingEndpointHealthSignal(WarpEventType.WebhookEndpointFailing, groupName!, attemptedAt, attemptedAt);
                    }
                }
                else
                {
                    // Exhausted (covers the empty-schedule single-attempt case: attemptNumber 1 > count 0). The
                    // callback-pending flag commits WITH the transition so a crash before the callback is recovered.
                    delivery.Status = WebhookDeliveryStatus.Exhausted;
                    delivery.ExhaustedCallbackPending = true;
                    WarpTelemetry.WebhookDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", "exhausted"));
                    exhausted = true;
                }
            }

            // Commit the state transition (and any scheduled retry job) FIRST so the exhausted callback below
            // never fires ahead of a persisted Exhausted row that a rollback could undo.
            await _publisher.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
#pragma warning disable CA1031 // executor jobs must ALWAYS complete: a persistence fault is logged, never a failed job.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Webhook delivery {DeliveryId} could not persist its attempt {Attempt} outcome; completing the executor job.", delivery.Id, attemptNumber);

            return;
        }

        // The endpoint health signal first: it is best effort and raised once, so a fault in the exhausted-callback
        // step below (which has its own crash recovery) must not lose it.
        if (healthSignal is not null)
        {
            await WebhookEndpointHealthSignals.RaiseAsync(healthSignal, _endpointHealthHandlers, _notifier, _logger);
        }

        // Post-commit: the Exhausted row is durable, so invoke the host callback now (guarded), then clear the
        // callback-pending flag in a second small commit. At-least-once on a crash between the two commits —
        // the recovery path at the top of ExecuteAsync re-runs the (idempotent) callback.
        if (exhausted)
        {
            await InvokeExhaustedHandlersAsync(delivery, delivery.AttemptCount, cancellationToken);
            delivery.ExhaustedCallbackPending = false;
            await _publisher.SaveChangesAsync(cancellationToken);
        }
    }

    private enum EndpointOutcome
    {
        NotTracked = 1,
        StartedFailing = 2,
        AlreadyFailing = 3,
    }

    // Creates the endpoint row (Healthy) if it does not exist yet, in its own scope and transaction, so a
    // duplicate-key race with another executor is a caught failure here instead of an aborted outcome transaction.
    private async Task EnsureEndpointAsync(string groupName, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();

            var exists = await context.Set<WebhookEndpoint>()
                .AsNoTracking()
                .Where(x => x.Name == groupName)
                .AnyAsync(cancellationToken);

            if (exists)
            {
                return;
            }

            context.Set<WebhookEndpoint>().Add(new WebhookEndpoint
            {
                Name = groupName,
                State = WebhookEndpointState.Healthy,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            });

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another executor created it first — exactly what was needed.
        }
    }

    // A tracked success recovers a Failing endpoint, and the endpoint monitor then releases its parked deliveries.
    // Otherwise only the last-success time moves, and an endpoint that never failed has no row to update.
    private async Task<PendingEndpointHealthSignal?> RecordEndpointSuccessAsync(string groupName, DateTime at, CancellationToken cancellationToken)
    {
        // The common case first, in one statement: a Healthy endpoint only moves its last-success time.
        var healthy = await _context.Set<WebhookEndpoint>()
            .Where(x => x.Name == groupName)
            .Where(x => x.State == WebhookEndpointState.Healthy)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSuccessAt, at), cancellationToken);

        if (healthy == 1)
        {
            return null;
        }

        var failing = await _context.Set<WebhookEndpoint>()
            .AsNoTracking()
            .Where(x => x.Name == groupName)
            .Where(x => x.State == WebhookEndpointState.Failing)
            .Select(x =>
                new
                {
                    x.FailingSince,
                })
            .FirstOrDefaultAsync(cancellationToken);

        // No row: the endpoint never failed, there is nothing to record.
        if (failing is null)
        {
            return null;
        }

        var recovered = await _context.Set<WebhookEndpoint>()
            .Where(x => x.Name == groupName)
            .Where(x => x.State == WebhookEndpointState.Failing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.State, WebhookEndpointState.Healthy)
                    .SetProperty(x => x.FailingSince, (DateTime?)null)
                    .SetProperty(x => x.NextProbeAt, (DateTime?)null)
                    .SetProperty(x => x.StillFailingNotifiedAt, (DateTime?)null)
                    .SetProperty(x => x.LastSuccessAt, at),
                cancellationToken);

        return recovered == 1
            ? new PendingEndpointHealthSignal(WarpEventType.WebhookEndpointRecovered, groupName, failing.FailingSince, at)
            : null;
    }

    private Task<int> RecordEndpointFailureAsync(string groupName, DateTime at, CancellationToken cancellationToken)
        => _context.Set<WebhookEndpoint>()
            .Where(x => x.Name == groupName)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastFailureAt, at), cancellationToken);

    // A tracked delivery ran out of retries: a Healthy endpoint turns Failing (StartedFailing), an endpoint already
    // Failing stays so; either way the delivery parks. With no endpoint row at all (its creation failed for another
    // reason) the delivery is exhausted rather than parked where nothing would ever release it.
    private async Task<EndpointOutcome> MarkEndpointFailedAsync(string groupName, DateTime at, CancellationToken cancellationToken)
    {
        var nextProbeAt = at + _probeInterval;
        var startedFailing = await _context.Set<WebhookEndpoint>()
            .Where(x => x.Name == groupName)
            .Where(x => x.State == WebhookEndpointState.Healthy)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.State, WebhookEndpointState.Failing)
                    .SetProperty(x => x.FailingSince, at)
                    .SetProperty(x => x.NextProbeAt, nextProbeAt)
                    .SetProperty(x => x.StillFailingNotifiedAt, (DateTime?)null)
                    .SetProperty(x => x.LastFailureAt, at),
                cancellationToken);

        if (startedFailing == 1)
        {
            return EndpointOutcome.StartedFailing;
        }

        var existing = await RecordEndpointFailureAsync(groupName, at, cancellationToken);

        return existing == 1 ? EndpointOutcome.AlreadyFailing : EndpointOutcome.NotTracked;
    }

    // Atomically claims one delivery attempt: increments AttemptCount (and stamps NextAttemptAt for the retry
    // this attempt will schedule on failure, so the two columns stay consistent) only while the row is still
    // Pending and still carries the AttemptCount this executor loaded. Exactly one of N concurrent executors
    // for the same (Pending, AttemptCount) matches a row (returns true); the losers match zero rows (return
    // false) and complete quietly. Internal + static so the concurrency guard is testable directly.
    internal static async Task<bool> TryClaimAttemptAsync(DbContext context, Guid deliveryId, int loadedAttemptCount, DateTime? nextAttemptAt, CancellationToken ct)
    {
        var claimed = await context.Set<WebhookDelivery>()
            .Where(x => x.Id == deliveryId)
            .Where(x => x.Status == WebhookDeliveryStatus.Pending)
            .Where(x => x.AttemptCount == loadedAttemptCount)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.NextAttemptAt, nextAttemptAt),
                ct);

        return claimed == 1;
    }

    private async Task<AttemptResult> TryDeliverAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        // Each attempt is one warp-webhooks adapter call (group = endpoint, operation = event type,
        // correlation = delivery id) — the attempt timeline is AdapterCallLog WHERE CorrelationId =
        // deliveryId, no separate table. A manual scope (not the Warp.Adapters.Http auto-recording handler)
        // keeps webhooks free of any binding-package dependency; telemetry flows unconditionally (§2.15)
        // and a row is written only where AddAdapters() enabled DB recording. A pre-HTTP fault (bad URL,
        // signing) fails the same scope, so the timeline is never empty.
        using var scope = _adapters.BeginCall(WebhookConstants.AdapterName, delivery.EventType, delivery.GroupName);
        scope.SetCorrelation(delivery.Id.ToString());

        HttpRequestMessage? request = null;
        try
        {
            request = BuildRequest(delivery);
            ApplySigning(request, delivery);
        }
#pragma warning disable CA1031 // executor jobs must ALWAYS complete: a build/signing fault is a failed attempt, never a failed job.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            request?.Dispose();
            scope.Fail(ex);
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} attempt {Attempt} failed before the HTTP request was sent.", delivery.Id, delivery.AttemptCount + 1);

            return AttemptResult.Failed(null, WebhookFailureReason.Other);
        }

        // The per-attempt budget covers the send and the bounded body read. It is a linked token rather than
        // the client's own Timeout property, because a host may configure its own warp-webhooks client and
        // Warp must not overwrite that client's settings. A timeout is an ordinary failed attempt (retried
        // per schedule); host shutdown stays distinguishable through the job's own token.
        using var timeoutCts = new CancellationTokenSource(_attemptTimeout, _timeProvider);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var attemptToken = attemptCts.Token;

        try
        {
            var client = _httpClientFactory.CreateClient(WebhookConstants.AdapterName);

            // ResponseHeadersRead: the status decides the outcome, so the body is never buffered. Only the
            // capture prefix is read (when capture is on) and the rest is never drained; disposing the
            // response releases the connection.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken);

            var success = IsSuccess(response, delivery);
            await CaptureResponseAsync(scope, request, response, isFailure: !success, attemptToken, cancellationToken);

            if (success)
            {
                scope.Succeed();

                return new AttemptResult(true, (int)response.StatusCode, null);
            }

            scope.Fail(new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null,
                statusCode: response.StatusCode));

            return AttemptResult.Failed((int)response.StatusCode, WebhookFailureReason.UnsuccessfulStatus);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebhookAddressNotPublicException notPublic)
        {
            // PublicAddressesOnly refused the connection. The reason names the host, never its addresses.
            scope.Fail(notPublic);
            _logger.LogWarning("Webhook delivery {DeliveryId} attempt {Attempt} failed: {Reason}", delivery.Id, delivery.AttemptCount + 1, notPublic.Message);

            return AttemptResult.Failed(null, WebhookFailureReason.AddressNotPublic);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            scope.Fail(ex);
            _logger.LogWarning("Webhook delivery {DeliveryId} attempt {Attempt} was cancelled by the executor job's token (host shutdown or job cancellation), not by the attempt timeout.", delivery.Id, delivery.AttemptCount + 1);

            return AttemptResult.Failed(null, WebhookFailureReason.Other);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"The webhook attempt did not complete within {_attemptTimeout} (WebhookAttemptTimeout).");
            scope.Fail(timeout);
            _logger.LogWarning("Webhook delivery {DeliveryId} attempt {Attempt} timed out after {Timeout}.", delivery.Id, delivery.AttemptCount + 1, _attemptTimeout);

            return AttemptResult.Failed(null, WebhookFailureReason.Timeout);
        }
#pragma warning disable CA1031 // executor jobs must ALWAYS complete: every attempt exception is a failed attempt, never a failed job.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            scope.Fail(ex);
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} attempt {Attempt} failed.", delivery.Id, delivery.AttemptCount + 1);

            return AttemptResult.Failed(null, ClassifyFailure(ex));
        }
        finally
        {
            request.Dispose();
        }
    }

    // Maps a transport exception to the #357 failure reason. The HttpClient's own timeout surfaces as a
    // TaskCanceledException wrapping a TimeoutException; everything else not recognised is ConnectionFailed for an
    // HttpRequestException and Other for anything else.
    internal static WebhookFailureReason ClassifyFailure(Exception exception)
    {
        if (exception is TaskCanceledException { InnerException: TimeoutException })
        {
            return WebhookFailureReason.Timeout;
        }

        if (exception is not HttpRequestException http)
        {
            return WebhookFailureReason.Other;
        }

        if (FindInner<WebhookAddressNotPublicException>(http) is not null)
        {
            return WebhookFailureReason.AddressNotPublic;
        }

        if (FindInner<SocketException>(http) is { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return WebhookFailureReason.ConnectionRefused;
        }

        return http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => WebhookFailureReason.DnsFailure,
            HttpRequestError.SecureConnectionError => WebhookFailureReason.TlsError,
            _ => WebhookFailureReason.ConnectionFailed,
        };
    }

    private static T? FindInner<T>(Exception exception)
        where T : Exception
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    // Applies the warp-webhooks adapter's capture tiers to the completed attempt. Status is always-recorded
    // metadata (§8.19); response headers/body follow the resolved CaptureMode (webhooks defaults to
    // response-body Always, request-body never — the payload already lives on the delivery row). Values are
    // redacted (§1.2) and byte-truncated before they reach the scope's capture columns. The executor owns
    // the response, so the body is read destructively (no downstream consumer to preserve it for). The
    // response was sent with ResponseHeadersRead, so only the capped prefix ever crosses the wire into
    // memory; with body capture off for this outcome the body is not read at all. The read is bounded by
    // the attempt timeout, but running out of time there only abandons the capture: the outcome was already
    // decided by the status code, and turning a 2xx into a retry would send the event again.
    private async Task CaptureResponseAsync(AdapterCallScope scope, HttpRequestMessage request, HttpResponseMessage response, bool isFailure, CancellationToken attemptToken, CancellationToken jobToken)
    {
        scope.SetStatusCode((int)response.StatusCode);

        var recording = _adapterRegistry.Resolve(WebhookConstants.AdapterName);
        scope.SetRequestSummary($"{request.Method.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}");

        if (HttpCaptureHelpers.ShouldCapture(recording.CaptureHeaders, isFailure))
        {
            scope.SetResponseHeaders(HttpCaptureHelpers.RedactHeaders(AllHeaders(response.Headers, response.Content?.Headers), recording.RedactedHeaders, recording.MaxCapturedHeaderSize));
        }

        if (response.Content is not null && HttpCaptureHelpers.ShouldCapture(recording.CaptureResponseBodies, isFailure))
        {
            var body = await ReadResponseBodyAsync(response.Content, recording.MaxCapturedBodySize, attemptToken, jobToken);
            if (body is not null)
            {
                scope.SetResponseBody(HttpCaptureHelpers.TruncateToBytes(body, recording.MaxCapturedBodySize));
            }
        }
    }

    private static async Task<string?> ReadResponseBodyAsync(HttpContent content, int maxBytes, CancellationToken attemptToken, CancellationToken jobToken)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        try
        {
            await using var stream = await content.ReadAsStreamAsync(attemptToken);

            // Read only a bounded prefix (+1 byte so an over-cap body still trips TruncateToBytes' marker).
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), attemptToken);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (OperationCanceledException) when (!jobToken.IsCancellationRequested)
        {
            // The attempt timeout fired mid-read: abandon the capture, keep the status-decided outcome.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capture is best-effort and must never fail the attempt.
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders(HttpHeaders primary, HttpHeaders? content)
    {
        foreach (var header in primary)
        {
            yield return header;
        }

        if (content is null)
        {
            yield break;
        }

        foreach (var header in content)
        {
            yield return header;
        }
    }

    // attemptCount is passed explicitly rather than read from the row: on the post-Redeliver recovery path
    // the live column has already been reset to 0, and the snapshot must report the exhaustion's own count.
    private async Task InvokeExhaustedHandlersAsync(WebhookDelivery delivery, int attemptCount, CancellationToken cancellationToken)
    {
        var snapshot = new WebhookDeliveryExhausted
        {
            DeliveryId = delivery.Id,
            EventType = delivery.EventType,
            EventId = delivery.EventId,
            Url = delivery.Url,
            GroupName = delivery.GroupName,
            Reference = delivery.Reference,
            AttemptCount = attemptCount,
        };

        foreach (var handler in _exhaustedHandlers)
        {
            try
            {
                await handler.OnDeliveryExhaustedAsync(snapshot, cancellationToken);
            }
#pragma warning disable CA1031 // host callback: a throwing handler is logged and never propagated to the job.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "IWebhookDeliveryExhaustedHandler threw for delivery {DeliveryId}; delivery stays Exhausted.", delivery.Id);
            }
        }

        // Also surface the exhaustion as a generic operational event (§8.25). The webhook-specific handler
        // above and this notifier coexist: the former is a webhook integration hook, the latter the
        // process-wide alerting seam. Same redaction-safe snapshot; the dispatcher guards each notifier.
        await _notifier.DispatchAsync(
            new WebhookDeliveryExhaustedEvent
            {
                Type = WarpEventType.WebhookDeliveryExhausted,
                Severity = WarpEventSeverity.Warning,
                TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
                MachineName = Environment.MachineName,
                Application = WarpTelemetry.ApplicationName,
                Message = $"Webhook delivery {snapshot.DeliveryId} to {snapshot.GroupName ?? snapshot.Url} exhausted after {snapshot.AttemptCount} attempt(s).",
                DeliveryId = snapshot.DeliveryId,
                EventType = snapshot.EventType,
                EventId = snapshot.EventId,
                Url = snapshot.Url,
                GroupName = snapshot.GroupName,
                Reference = snapshot.Reference,
                AttemptCount = snapshot.AttemptCount,
            },
            CancellationToken.None); // None: don't abandon an already-committed exhaustion alert on shutdown.
    }

    // Signs the attempt per the delivery's SigningMode. None adds nothing; StandardWebhooks uses the
    // built-in signer; Custom uses the host's registered IWebhookSigner (presence is validated at
    // AddWebhooks time, §requirement — the null-guard here is defense-in-depth). Runs inside
    // TryDeliverAsync's try, so any signing fault is recorded as a failed attempt, never a failed job.
    private void ApplySigning(HttpRequestMessage request, WebhookDelivery delivery)
    {
        var signer = ResolveSigner(delivery.SigningMode);
        if (signer is null)
        {
            return;
        }

        var headers = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = delivery.EventId,
            Timestamp = _timeProvider.GetUtcNow(),
            Payload = delivery.PayloadJson,
            Secret = delivery.Secret,
        });

        foreach (var header in headers)
        {
            AddHeader(request, header.Key, header.Value);
        }
    }

    private IWebhookSigner? ResolveSigner(WebhookSigning mode)
    {
        return mode switch
        {
            WebhookSigning.StandardWebhooks => _standardSigner,
            WebhookSigning.Custom => _customSigners.FirstOrDefault() ?? throw new InvalidOperationException(
                "A delivery used WebhookSigning.Custom but no IWebhookSigner is registered. "
                + "Register one via AddWebhooks(w => w.UseCustomSigner<T>())."),
            _ => null,
        };
    }

    private HttpRequestMessage BuildRequest(WebhookDelivery delivery)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json"),
        };

        ApplyHeaders(request, delivery);

        // Signing (W3): StandardWebhooks/Custom add the webhook-id/webhook-timestamp/webhook-signature
        // headers via IWebhookSigner in ApplySigning. The operation/group/correlation that used to ride the
        // request (for the auto-recording handler) now go straight to the manual BeginCall scope, so the
        // request carries only what the destination sees.
        return request;
    }

    private void ApplyHeaders(HttpRequestMessage request, WebhookDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(delivery.HeadersJson))
        {
            return;
        }

        Dictionary<string, string>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(delivery.HeadersJson);
        }
        catch (JsonException ex)
        {
            // Degrade — deliver without custom headers — but surface it: a malformed HeadersJson blob is a
            // silent data-quality bug otherwise (§8.13 stored data is host-controlled). No PII: only the id.
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} has malformed HeadersJson; delivering without custom headers.", delivery.Id);

            return;
        }

        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            AddHeader(request, header.Key, header.Value);
        }
    }

    // Try request headers first; content headers (e.g. Content-Type) are already set by StringContent,
    // so a rejected add there is harmless and intentionally ignored.
    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private bool IsSuccess(HttpResponseMessage response, WebhookDelivery delivery)
    {
        if (string.IsNullOrEmpty(delivery.SuccessCodesJson))
        {
            return response.IsSuccessStatusCode;
        }

        int[]? codes;
        try
        {
            codes = JsonSerializer.Deserialize<int[]>(delivery.SuccessCodesJson);
        }
        catch (JsonException ex)
        {
            // Degrade to the default 2xx check but surface the malformed SuccessCodesJson (§8.13). No PII.
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} has malformed SuccessCodesJson; falling back to any-2xx success.", delivery.Id);

            return response.IsSuccessStatusCode;
        }

        return codes is not null && codes.Contains((int)response.StatusCode);
    }
}

// The result of one attempt: success, the response status (null without a response) and, on failure, why (#357).
internal sealed record AttemptResult(bool Succeeded, int? StatusCode, WebhookFailureReason? FailureReason)
{
    public static AttemptResult Failed(int? statusCode, WebhookFailureReason reason) => new(false, statusCode, reason);
}
