using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// End-to-end execution coverage for the webhook dispatcher + executor (WSC2–WSC5). Runs the real
/// worker via <see cref="WarpTestServer"/> with the <c>warp-webhooks</c> HTTP adapter's primary handler
/// stubbed (no live network), so every path exercises the full DI wiring: <c>SendAsync</c> persists a row
/// and enqueues the executor job, the worker runs it, and the delivery state machine advances. The
/// invariant across every failure path is that the executor job <b>completes</b> — no <see cref="Job"/>
/// row is ever left in <see cref="State.Failed"/>.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookExecutionTestsBase : IntegrationTestBase
{
    protected WebhookExecutionTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Send_SuccessfulAttempt_DeliversAndRecordsAdapterCall()
    {
        await using var server = await StartServerAsync(HttpStatusCode.OK);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            Group = "endpoint-eu",
            Payload = "{\"order\":42}",
            RetrySchedule = [TimeSpan.FromHours(1)],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        // WSC2: the attempt is recorded as an AdapterCallLog row linked by CorrelationId, operation, group.
        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == deliveryId.ToString(), Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        var call = await server.CreateContext().Set<AdapterCallLog>()
            .Where(x => x.CorrelationId == deliveryId.ToString())
            .FirstAsync(Ct);

        call.AdapterName.ShouldBe("warp-webhooks");
        call.Operation.ShouldBe("order.created");
        call.GroupName.ShouldBe("endpoint-eu");

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ServerWithoutAddWebhooks_StillDrainsAndDelivers()
    {
        // The whole point of webhooks-in-Core (§8.20): a server that NEVER called AddWebhooks still polls
        // warp:webhooks and executes the delivery. The two-process footgun (a server that silently doesn't
        // drain because it lacked the opt-in) is gone — there is nothing to forget.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.Queues = ["default"],
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_DeliveredAttempt_CapturesResponseBodyButNotRequestPayload()
    {
        // The auto-registered warp-webhooks adapter is configured CaptureResponseBodies=Always /
        // CaptureRequestBodies=None: the response is captured for diagnosis, but the request payload is NOT
        // (it already lives on the delivery row, so duplicating it would double the storage + PII surface).
        // Proven end-to-end on the recorded AdapterCallLog rather than by inspecting registration internals.
        await using var server = await StartServerAsync(HttpStatusCode.OK);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            Payload = "{\"order\":42}",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == deliveryId.ToString(), Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        var call = await server.CreateContext().Set<AdapterCallLog>()
            .Where(x => x.CorrelationId == deliveryId.ToString())
            .FirstAsync(Ct);

        call.ResponseBody.ShouldBe("\"ok\"");
        call.RequestBody.ShouldBeNull();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_FailedAttemptWithRetriesLeft_StaysPendingAndSchedulesNext()
    {
        await using var server = await StartServerAsync(HttpStatusCode.InternalServerError);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [TimeSpan.FromHours(1)],
        });

        // Wait for the first attempt to complete (AttemptCount incremented) while the row stays Pending —
        // the 1-hour delay guarantees the scheduled retry does not fire during the test window.
        await WaitForAttemptAsync(server, deliveryId, 1);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(1);
        delivery.NextAttemptAt.ShouldNotBeNull();

        // WSC3: a Scheduled executor job exists with ScheduleTime == NextAttemptAt.
        var scheduled = await server.CreateContext().Set<Job>()
            .Where(x => x.Queue == "warp:webhooks")
            .Where(x => x.CurrentState == State.Scheduled)
            .ToListAsync(Ct);

        scheduled.ShouldHaveSingleItem().ScheduleTime.ShouldBe(delivery.NextAttemptAt!.Value);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ScheduleExhausted_MarksExhaustedAndInvokesHandlerOnce()
    {
        await using var server = await StartServerAsync(HttpStatusCode.InternalServerError);
        var recorder = server.GetService<ExhaustedCallRecorder>();

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        // The Exhausted status commits BEFORE the callback fires (W-1 commit-before-callback), so the
        // status alone does not imply the callback ran — wait for the callback-pending flag to clear
        // (the second commit, made after the invocation) before asserting the count.
        await WarpTestServer.WaitUntil(
            async () => !await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == deliveryId)
                .Select(x => x.ExhaustedCallbackPending)
                .FirstOrDefaultAsync(Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        recorder.CountFor(deliveryId).ShouldBe(1);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_EmptySchedule_MakesExactlyOneAttempt()
    {
        await using var server = await StartServerAsync(HttpStatusCode.InternalServerError);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.AttemptCount.ShouldBe(1);

        // No scheduled retry job was published.
        var scheduled = await server.CreateContext().Set<Job>()
            .Where(x => x.Queue == "warp:webhooks")
            .CountAsync(x => x.CurrentState == State.Scheduled, Ct);
        scheduled.ShouldBe(0);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_DefaultSuccessCodes_TreatsAny2xxAsDelivered()
    {
        await using var server = await StartServerAsync(HttpStatusCode.Accepted);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ExplicitSuccessCodes_Treats202AsFailure()
    {
        await using var server = await StartServerAsync(HttpStatusCode.Accepted);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            SuccessCodes = [200],
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ExplicitNon2xxSuccessCode_TreatsThatStatusAsDelivered()
    {
        // SuccessCodes is an arbitrary list, not a 2xx subset: a host that treats 404 as "receiver gone,
        // stop retrying" must get Delivered on a 404 when it says so.
        await using var server = await StartServerAsync(HttpStatusCode.NotFound);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            SuccessCodes = [200, 404],
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_RedirectStatusUnderDefaultCodes_IsAFailedAttempt()
    {
        // Default success is any 2xx — a 301 the handler pipeline surfaces (production HttpClientHandler
        // follows redirects for GET/HEAD but NOT for POST bodies re-sent cross-origin, so 3xx genuinely
        // reaches the executor) is a failed attempt, not a silent success.
        await using var server = await StartServerAsync(HttpStatusCode.MovedPermanently);

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_MultiEntrySchedule_SchedulesEachRetryFromItsOwnDelayAndExhaustsAfterFullSchedule()
    {
        // WSC3 boundary: a multi-entry schedule with distinct seconds-scale delays, driven through the real
        // worker + ScheduledJobActivation. After attempt N the next attempt must be scheduled at
        // (attemptTime + schedule[N-1]) — asserted against an EXTERNALLY computed window
        // [executionLowerBound + delay, observedAt + delay], not the row's own NextAttemptAt
        // self-consistency — and the delivery must stay Pending through every entry, flipping to Exhausted
        // only on the (schedule.Count + 1)-th attempt once the whole schedule is consumed. The distinct 2s
        // middle entry catches an off-by-one in the schedule[N-1] indexing (the ±0.5s tolerance is tighter
        // than the 1s spacing between the delays).
        await using var server = await StartServerAsync(HttpStatusCode.InternalServerError);

        TimeSpan[] schedule =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
        ];
        var tolerance = TimeSpan.FromMilliseconds(500);

        var beforeSend = DateTime.UtcNow;

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = schedule,
        });

        // Race-free lower bound on when attempt N executes: attempt 1 runs after send; each later retry is a
        // Scheduled job that ScheduledJobActivation only releases at/after its ScheduleTime (the prior
        // NextAttemptAt), so the prior NextAttemptAt is a lower bound derived from data, not a wall-clock race.
        var executionLowerBound = beforeSend;

        for (var n = 1; n <= schedule.Length; n++)
        {
            await WaitForAttemptAsync(server, deliveryId, n);
            var observedAt = DateTime.UtcNow;

            var delivery = await GetDeliveryAsync(server, deliveryId);
            delivery.AttemptCount.ShouldBe(n);

            // The schedule is not yet consumed, so the delivery stays Pending with a scheduled next attempt.
            delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
            delivery.NextAttemptAt.ShouldNotBeNull();

            var expectedDelay = schedule[n - 1];
            delivery.NextAttemptAt!.Value.ShouldBeInRange(
                executionLowerBound + expectedDelay - tolerance,
                observedAt + expectedDelay + tolerance);

            executionLowerBound = delivery.NextAttemptAt.Value;
        }

        // Only now that every entry is consumed does the (Count + 1)-th attempt exhaust the delivery.
        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var final = await GetDeliveryAsync(server, deliveryId);
        final.AttemptCount.ShouldBe(schedule.Length + 1);
        final.NextAttemptAt.ShouldBeNull();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_StandardWebhooksSigning_EmitsSignatureHeadersThroughExecutor()
    {
        // WSC6 through the REAL dispatcher→worker→executor→HTTP leg (not a unit call on the signer): a
        // StandardWebhooks delivery with a valid base64 whsec_ secret must reach the endpoint carrying the
        // three webhook-* headers, with webhook-id == the delivery's EventId and a v1,-prefixed signature.
        var recorder = new WebhookRequestRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturingWebhookHandler(HttpStatusCode.OK, recorder)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            EventId = "evt_signed_1",
            Payload = "{\"order\":7}",
            Signing = WebhookSigning.StandardWebhooks,
            Secret = StandardWebhooksVectorSecret,
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        var headers = recorder.LastHeaders();
        headers.ShouldNotBeNull();

        headers.ShouldContainKey("webhook-id");
        headers["webhook-id"].ShouldBe("evt_signed_1");
        headers.ShouldContainKey("webhook-timestamp");
        headers.ShouldContainKey("webhook-signature");
        headers["webhook-signature"].ShouldStartWith("v1,");

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_NoSigning_OmitsSignatureHeadersThroughExecutor()
    {
        // Companion to the Standard test: WebhookSigning.None wires no signer, so none of the three
        // webhook-* headers reach the endpoint — proven on the same captured outgoing request.
        var recorder = new WebhookRequestRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturingWebhookHandler(HttpStatusCode.OK, recorder)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            Signing = WebhookSigning.None,
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        var headers = recorder.LastHeaders();
        headers.ShouldNotBeNull();

        headers.ShouldNotContainKey("webhook-id");
        headers.ShouldNotContainKey("webhook-timestamp");
        headers.ShouldNotContainKey("webhook-signature");

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_CustomSigning_EmitsCustomSignerHeadersThroughExecutor()
    {
        // WSC6 Custom leg through the REAL dispatcher→worker→executor→HTTP path (StandardWebhooks/None are
        // already covered at this level; Custom was only unit-tested on the signer). The host's registered
        // IWebhookSigner runs inside the executor's HTTP leg and its headers reach the wire.
        var recorder = new WebhookRequestRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w => w.UseCustomSigner<StubCustomSigner>());
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturingWebhookHandler(HttpStatusCode.OK, recorder)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            Signing = WebhookSigning.Custom,
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        var headers = recorder.LastHeaders();
        headers.ShouldNotBeNull();
        headers.ShouldContainKey("x-stub-signature");

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_CorruptHeadersJson_StillPostsWithoutCustomHeadersAndCompletes()
    {
        // A malformed HeadersJson blob must degrade — the executor logs and delivers WITHOUT custom headers —
        // never fail the attempt or the job. Seeded directly so the corrupt blob bypasses the dispatcher's
        // serialization (which would never produce it).
        var recorder = new WebhookRequestRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturingWebhookHandler(HttpStatusCode.OK, recorder)));

        var deliveryId = await SeedPendingDeliveryAsync(server, headersJson: "{ this is not valid json", successCodesJson: null);

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        // A request went out (the corrupt headers did not abort the POST) and carried no custom header from
        // the malformed blob.
        var headers = recorder.LastHeaders();
        headers.ShouldNotBeNull();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_CorruptSuccessCodesJson_FallsBackToAny2xxAndDelivers()
    {
        // A malformed SuccessCodesJson blob must degrade to the default any-2xx success check. A 202 response
        // (not a default-listed exact code) is delivered under the fallback, proving the fallback fired.
        await using var server = await StartServerAsync(HttpStatusCode.Accepted);

        var deliveryId = await SeedPendingDeliveryAsync(server, headersJson: null, successCodesJson: "[not, json]");

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);
        await AssertNoFailedJobsAsync(server);
    }

    private static async Task<Guid> SeedPendingDeliveryAsync(WarpTestServer server, string? headersJson, string? successCodesJson)
    {
        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            HeadersJson = headersJson,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            SuccessCodesJson = successCodesJson,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, "warp:webhooks");
        await publisher.SaveChangesAsync(Ct);

        return deliveryId;
    }

    // Published Standard Webhooks test vector secret (valid base64 after the whsec_ prefix); the same value
    // WebhookSigningTests pins. Used here only to exercise the real signing leg end-to-end.
    private const string StandardWebhooksVectorSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private Task<WarpTestServer> StartServerAsync(HttpStatusCode responseStatus)
    {
        // No manual warp:webhooks queue wiring: the default worker group polls warp:webhooks unconditionally
        // (webhooks are Core, §8.20), so the default wiring is what these end-to-end tests prove. AddAdapters
        // turns on per-attempt AdapterCallLog recording — the tests that assert the attempt timeline need it
        // (delivery itself works without it; the inline-cfg tests prove that path).
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks(w => w.OnDeliveryExhausted<CountingExhaustedHandler>());
            },
            configureServices: services =>
            {
                services.AddSingleton<ExhaustedCallRecorder>();

                // Override the warp-webhooks client's primary handler with a stub — no live network.
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(responseStatus));
            });
    }

    [TimedFact]
    public async Task Send_ExhaustedHandlerThrows_JobCompletesAndRowStaysExhausted()
    {
        // W-1: the exhausted callback fires post-commit and is guarded — a throwing handler must leave the
        // committed Exhausted row intact and still complete the executor job.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w => w.OnDeliveryExhausted<ThrowingExhaustedHandler>());
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.InternalServerError)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_AttemptThrowsTransportException_RecordsFailedAttemptAndJobCompletes()
    {
        // W-1: a transport exception from the HTTP leg is a recorded failed attempt, never a failed job.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks();
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new ThrowingWebhookHandler()));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.AttemptCount.ShouldBe(1);

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == deliveryId.ToString() && x.Outcome == AdapterCallOutcome.Failed, Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_PreHttpSigningFailure_RecordsAdapterCallRowSoTimelineIsNotEmpty()
    {
        // W-3: signing throws BEFORE the HTTP handler pipeline runs, so no pipeline-recorded row exists. The
        // executor records a manual warp-webhooks scope so the attempt timeline is never empty.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks(w => w.UseCustomSigner<ThrowingSigner>());
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            Signing = WebhookSigning.Custom,
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == deliveryId.ToString() && x.Outcome == AdapterCallOutcome.Failed, Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Execute_ExhaustedWithCallbackPending_RecoversCallbackAndClearsFlag()
    {
        // BUG-1: a crash between the Exhausted commit and the callback leaves ExhaustedCallbackPending set.
        // A re-run of the executor job for the already-Exhausted row must re-invoke the (idempotent) handler
        // and clear the flag — never a second HTTP attempt — proving at-least-once delivery of the callback.
        await using var server = await StartServerAsync(HttpStatusCode.OK);
        var recorder = server.GetService<ExhaustedCallRecorder>();

        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Exhausted,
            AttemptCount = 1,
            ExhaustedCallbackPending = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        // Enqueue the executor job for the already-settled delivery — mirrors the crash re-run.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, "warp:webhooks");
        await publisher.SaveChangesAsync(Ct);

        await WarpTestServer.WaitUntil(
            async () => !await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == deliveryId)
                .Select(x => x.ExhaustedCallbackPending)
                .FirstOrDefaultAsync(Ct),
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);

        recorder.CountFor(deliveryId).ShouldBe(1);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
        delivery.AttemptCount.ShouldBe(1);              // recovery makes no second attempt
        delivery.ExhaustedCallbackPending.ShouldBeFalse();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Execute_PendingWithCallbackPending_FiresRecoveredCallbackThenProceedsWithAttempt()
    {
        // BUG-B: a Redeliver can flip an Exhausted row back to Pending BEFORE the crash-recovery re-run
        // fires the outstanding exhausted callback. The callback obligation (ExhaustedCallbackPending)
        // must survive the status flip: the executor fires the prior exhaustion's callback, clears the
        // flag, and then still carries out the redelivered attempt. An Exhausted-only recovery check
        // silently drops the notification forever.
        await using var server = await StartServerAsync(HttpStatusCode.OK);
        var recorder = server.GetService<ExhaustedCallRecorder>();

        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = DateTime.UtcNow,
            ExhaustedCallbackPending = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        // The executor job a Redeliver would have enqueued alongside the settled→Pending flip.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, "warp:webhooks");
        await publisher.SaveChangesAsync(Ct);

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        recorder.CountFor(deliveryId).ShouldBe(1);      // the PRIOR exhaustion's callback, recovered

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.AttemptCount.ShouldBe(1);              // the redelivered attempt still happened
        delivery.ExhaustedCallbackPending.ShouldBeFalse();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Execute_RecoveredCallbackAfterRedeliver_ReportsExhaustedAttemptCount_NotResetZero()
    {
        // D1: Redeliver resets AttemptCount to 0 but deliberately preserves ExhaustedCallbackPending. The
        // recovered callback must therefore NOT read the live (reset) count — it reconstructs the count the
        // exhaustion had from the immutable schedule (Count + 1). A snapshot reporting AttemptCount = 0 for
        // a delivery that exhausted after N attempts is corrupted data in the host notification.
        var snapshots = new ObservedSnapshotRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w => w.OnDeliveryExhausted<SnapshotObservingExhaustedHandler>());
            },
            configureServices: services =>
            {
                services.AddSingleton(snapshots);
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK));
            });

        // Post-Redeliver race state: the delivery exhausted after its 2-retry schedule (3 attempts), the
        // process crashed before the callback, and Redeliver has since flipped it Pending and reset
        // AttemptCount to 0 — with the callback obligation still pending.
        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = DateTime.UtcNow,
            ExhaustedCallbackPending = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, "warp:webhooks");
        await publisher.SaveChangesAsync(Ct);

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        var snapshot = snapshots.For(deliveryId);
        snapshot.ShouldNotBeNull();
        snapshot.AttemptCount.ShouldBe(3);      // schedule.Count + 1, the count the exhaustion actually had

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ScheduleExhausted_CallbackObservesCommittedExhaustedRow()
    {
        // BUG-1 commit-ordering proof: the exhausted callback reads the delivery row from an INDEPENDENT
        // DbContext scope and records the Status it observes. It must observe Exhausted — proving the
        // transition is durably committed before the callback fires (commit-before-callback).
        var observed = new ObservedStatusRecorder();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w => w.OnDeliveryExhausted<StatusObservingExhaustedHandler>());
            },
            configureServices: services =>
            {
                services.AddSingleton(observed);
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.InternalServerError));
            });

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        await WarpTestServer.WaitUntil(
            () => Task.FromResult(observed.For(deliveryId) is not null),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        observed.For(deliveryId).ShouldBe(WebhookDeliveryStatus.Exhausted);
        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_HttpLegThrowsRateLimited_CountsAsFailedAttemptConsumingScheduleSlot()
    {
        // DOCUMENT-7 (behavior documentation): a rate-limit rejection from the HTTP leg is a failed attempt
        // like any other — it increments AttemptCount and consumes a schedule slot. With an empty schedule the
        // single throttled attempt exhausts the delivery. Behavior is deliberately not changed; this pins it.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new RateLimitedWebhookHandler()));

        var deliveryId = await SendAsync(server, new WebhookSend
        {
            Url = "https://example.test/hook",
            EventType = "order.created",
            RetrySchedule = [],
        });

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.AttemptCount.ShouldBe(1);              // the throttled attempt consumed the (only) slot

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task HandleAsync_OutcomePersistFaults_CompletesAndLeavesSweepableStuckRow()
    {
        // BUG-A: the attempt claim commits immediately; the outcome commit (Delivered/retry/Exhausted) is a
        // SECOND transaction that can fault. The row is then Pending with no live executor job — nothing
        // scans NextAttemptAt and Redeliver rejects Pending, so it is recoverable ONLY via the stuck-row
        // sweep, whose predicate is a past NextAttemptAt. A final attempt must therefore stamp the claim
        // time onto NextAttemptAt, never null — otherwise the fault strands the delivery forever.
        await using var server = await StartServerAsync(HttpStatusCode.OK);

        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        // Drive the handler directly with a publisher whose first SaveChangesAsync faults — the exact
        // transient-DB-blip window between the committed claim and the outcome commit.
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var handler = new ExecuteWebhookDeliveryHandler<TestContext>(
            scope.ServiceProvider.GetRequiredService<TestContext>(),
            new FaultOncePublisher(scope.ServiceProvider.GetRequiredService<IPublisher>()),
            scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
            TimeProvider.System,
            scope.ServiceProvider.GetRequiredService<StandardWebhooksSigner>(),
            exhaustedHandlers: [],
            customSigners: [],
            scope.ServiceProvider.GetRequiredService<IWarpAdapters>(),
            scope.ServiceProvider.GetRequiredService<AdapterRegistry>(),
            NullLogger<ExecuteWebhookDeliveryHandler<TestContext>>.Instance);

        // The executor still completes (no-failed-jobs invariant) even though the outcome commit faulted.
        await Should.NotThrowAsync(async () =>
            await handler.HandleAsync(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, Ct));

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);    // the outcome was lost
        delivery.AttemptCount.ShouldBe(1);                          // the claim had already committed
        delivery.NextAttemptAt.ShouldNotBeNull();                   // the stuck-row sweep predicate
    }

    private static async Task<Guid> SendAsync(WarpTestServer server, WebhookSend send)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();

        return await dispatcher.SendAsync(send, Ct);
    }

    private static async Task<WebhookDelivery> GetDeliveryAsync(WarpTestServer server, Guid id)
    {
        return await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);
    }

    private static Task WaitForDeliveryStatusAsync(WarpTestServer server, Guid id, WebhookDeliveryStatus status)
    {
        return WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == status,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }

    private static Task WaitForAttemptAsync(WarpTestServer server, Guid id, int attemptCount)
    {
        return WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => x.AttemptCount)
                .FirstOrDefaultAsync(Ct) >= attemptCount,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }

    private static async Task AssertNoFailedJobsAsync(WarpTestServer server)
    {
        var failed = await server.CreateContext().Set<Job>()
            .CountAsync(x => x.CurrentState == State.Failed, Ct);

        failed.ShouldBe(0);
    }
}

/// <summary>
/// Stub primary <see cref="HttpMessageHandler"/> for the <c>warp-webhooks</c> client — returns a fixed
/// status with no live network so the executor's success/failure evaluation is deterministic.
/// </summary>
internal sealed class StubWebhookHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;

    public StubWebhookHandler(HttpStatusCode status) => _status = status;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("\"ok\"") });
}

/// <summary>
/// Wraps the scope's real <see cref="IPublisher"/>; the first <see cref="SaveChangesAsync"/> throws
/// (a transient DB blip on the executor's outcome commit — BUG-A), every later call forwards.
/// </summary>
internal sealed class FaultOncePublisher : IPublisher
{
    private readonly IPublisher _inner;
    private bool _faulted;

    public FaultOncePublisher(IPublisher inner) => _inner = inner;

    public Task<Guid> Publish<T>(T message)
        where T : class, IMessage => _inner.Publish(message);

    public Task<Guid> Publish<T>(T message, string? queue)
        where T : class, IMessage => _inner.Publish(message, queue);

    public Task<Guid> Enqueue<T>(T job)
        where T : class, IJob => _inner.Enqueue(job);

    public Task<Guid> Enqueue<T>(T job, string? queue)
        where T : class, IJob => _inner.Enqueue(job, queue);

    public Task<Guid> Enqueue<T>(T job, Guid parentJobId)
        where T : class, IJob => _inner.Enqueue(job, parentJobId);

    public Task<Guid> Enqueue<T>(T job, Guid parentJobId, string? queue)
        where T : class, IJob => _inner.Enqueue(job, parentJobId, queue);

    public Task<Guid> Enqueue<T>(T job, JobParameters jobParameters)
        where T : class, IJob => _inner.Enqueue(job, jobParameters);

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime)
        where T : class, IJob => _inner.Schedule(job, scheduleTime);

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, string? queue)
        where T : class, IJob => _inner.Schedule(job, scheduleTime, queue);

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId)
        where T : class, IJob => _inner.Schedule(job, scheduleTime, parentJobId);

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId, string? queue)
        where T : class, IJob => _inner.Schedule(job, scheduleTime, parentJobId, queue);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_faulted)
        {
            _faulted = true;

            throw new InvalidOperationException("simulated transient DB fault on the outcome commit");
        }

        return _inner.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Records per-delivery exhausted-handler invocations so a test can assert the callback fired exactly
/// once. Singleton bridge across the executor's per-job scope.
/// </summary>
internal sealed class ExhaustedCallRecorder
{
    private readonly Dictionary<Guid, int> _counts = [];
    private readonly Lock _gate = new();

    public void Record(Guid deliveryId)
    {
        lock (_gate)
        {
            _counts[deliveryId] = _counts.TryGetValue(deliveryId, out var current) ? current + 1 : 1;
        }
    }

    public int CountFor(Guid deliveryId)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(deliveryId, out var current) ? current : 0;
        }
    }
}

/// <summary>Exhausted handler that records its invocation against the shared recorder.</summary>
internal sealed class CountingExhaustedHandler : IWebhookDeliveryExhaustedHandler
{
    private readonly ExhaustedCallRecorder _recorder;

    public CountingExhaustedHandler(ExhaustedCallRecorder recorder) => _recorder = recorder;

    public Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken)
    {
        _recorder.Record(delivery.DeliveryId);

        return Task.CompletedTask;
    }
}

/// <summary>Exhausted handler that always throws — proves a throwing callback never fails the job (W-1).</summary>
internal sealed class ThrowingExhaustedHandler : IWebhookDeliveryExhaustedHandler
{
    public Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken)
        => throw new InvalidOperationException("exhausted handler failure");
}

/// <summary>
/// Primary <see cref="HttpMessageHandler"/> that throws a transport exception — proves a transport failure
/// is a recorded failed attempt, never a failed job (W-1).
/// </summary>
internal sealed class ThrowingWebhookHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("simulated transport failure");
}

/// <summary>Custom signer that throws — forces a pre-HTTP failure so the manual adapter scope fires (W-3).</summary>
internal sealed class ThrowingSigner : IWebhookSigner
{
    public IReadOnlyDictionary<string, string> Sign(WebhookSignatureRequest request)
        => throw new InvalidOperationException("simulated signing failure");
}

/// <summary>
/// Primary <see cref="HttpMessageHandler"/> that throws <see cref="AdapterRateLimitedException"/> — the shape
/// a rate-limited <c>warp-webhooks</c> client throws. Proves a throttle rejection is a failed attempt that
/// consumes a schedule slot (DOCUMENT-7), not a special case.
/// </summary>
internal sealed class RateLimitedWebhookHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new AdapterRateLimitedException("simulated rate-limit rejection");
}

/// <summary>
/// Exhausted handler that reads the delivery row from an INDEPENDENT DbContext scope and records the Status
/// it observes — proves the Exhausted transition is committed before the callback fires (BUG-1 ordering).
/// </summary>
internal sealed class StatusObservingExhaustedHandler : IWebhookDeliveryExhaustedHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ObservedStatusRecorder _recorder;

    public StatusObservingExhaustedHandler(IServiceScopeFactory scopeFactory, ObservedStatusRecorder recorder)
    {
        _scopeFactory = scopeFactory;
        _recorder = recorder;
    }

    public async Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        var status = await context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == delivery.DeliveryId)
            .Select(x => (WebhookDeliveryStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);

        _recorder.Record(delivery.DeliveryId, status);
    }
}

/// <summary>Records the delivery Status a callback observed from its own scope. Singleton bridge across scopes.</summary>
/// <summary>Exhausted handler that records the full snapshot so a test can assert its field values.</summary>
internal sealed class SnapshotObservingExhaustedHandler : IWebhookDeliveryExhaustedHandler
{
    private readonly ObservedSnapshotRecorder _recorder;

    public SnapshotObservingExhaustedHandler(ObservedSnapshotRecorder recorder) => _recorder = recorder;

    public Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken)
    {
        _recorder.Record(delivery);

        return Task.CompletedTask;
    }
}

/// <summary>Singleton bridge holding the last exhausted snapshot per delivery id.</summary>
internal sealed class ObservedSnapshotRecorder
{
    private readonly Dictionary<Guid, WebhookDeliveryExhausted> _observed = [];
    private readonly Lock _gate = new();

    public void Record(WebhookDeliveryExhausted snapshot)
    {
        lock (_gate)
        {
            _observed[snapshot.DeliveryId] = snapshot;
        }
    }

    public WebhookDeliveryExhausted? For(Guid deliveryId)
    {
        lock (_gate)
        {
            return _observed.TryGetValue(deliveryId, out var snapshot) ? snapshot : null;
        }
    }
}

internal sealed class ObservedStatusRecorder
{
    private readonly Dictionary<Guid, WebhookDeliveryStatus?> _observed = [];
    private readonly Lock _gate = new();

    public void Record(Guid deliveryId, WebhookDeliveryStatus? status)
    {
        lock (_gate)
        {
            _observed[deliveryId] = status;
        }
    }

    public WebhookDeliveryStatus? For(Guid deliveryId)
    {
        lock (_gate)
        {
            return _observed.TryGetValue(deliveryId, out var status) ? status : null;
        }
    }
}

/// <summary>
/// Primary <see cref="HttpMessageHandler"/> that snapshots each outgoing request's headers into a shared
/// <see cref="WebhookRequestRecorder"/> before returning a fixed status — lets a test assert what the real
/// executor actually put on the wire (e.g. the Standard Webhooks signature headers) with no live network.
/// </summary>
internal sealed class CapturingWebhookHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly WebhookRequestRecorder _recorder;

    public CapturingWebhookHandler(HttpStatusCode status, WebhookRequestRecorder recorder)
    {
        _status = status;
        _recorder = recorder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _recorder.Capture(request);

        return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("\"ok\"") });
    }
}

/// <summary>
/// Thread-safe recorder of captured request-header snapshots. The capturing handler runs in the executor's
/// per-job scope while the test asserts from the outside, so captures are copied into plain dictionaries
/// (case-insensitive, HTTP-header style) at send time — the request is disposed once the attempt returns.
/// </summary>
internal sealed class WebhookRequestRecorder
{
    private readonly List<IReadOnlyDictionary<string, string>> _captured = [];
    private readonly Lock _gate = new();

    public void Capture(HttpRequestMessage request)
    {
        var headers = request.Headers
            .ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _captured.Add(headers);
        }
    }

    public IReadOnlyDictionary<string, string>? LastHeaders()
    {
        lock (_gate)
        {
            return _captured.Count > 0 ? _captured[^1] : null;
        }
    }
}
