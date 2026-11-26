using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifiers;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Webhook endpoint health (8.1): a tracked delivery that runs out of retries marks its group Failing; while it fails,
/// deliveries that run out park; a success (a probe or any delivery) recovers the group and the endpoint monitor
/// releases what parked. The executor runs on the real worker against a switchable stub receiver; the monitor is
/// driven directly (its host interval is off) so each step is deterministic.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookEndpointHealthTestsBase : IntegrationTestBase
{
    private const string Group = "acme";
    private const string Url = "https://example.test/hook";

    protected WebhookEndpointHealthTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Send_TrackedDeliveryRunsOutOfRetries_MarksGroupFailingAndSignalsIt()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        var spy = new SpyNotifier();
        var health = new EndpointHealthRecorder();
        await using var server = await StartServerAsync(receiver, spy, health);

        var id = await SendAsync(server, "first");
        await WaitForStatusAsync(server, id, WebhookDeliveryStatus.Exhausted);
        await WaitUntilAsync(() => health.Count > 0);

        var endpoint = await GetEndpointAsync(Group);
        endpoint.State.ShouldBe(WebhookEndpointState.Failing);
        endpoint.FailingSince.ShouldNotBeNull();
        endpoint.NextProbeAt.ShouldNotBeNull().ShouldBeGreaterThan(endpoint.FailingSince!.Value);

        health.Kinds.ShouldBe(["failing"]);
        await WaitUntilAsync(() => spy.Received.OfType<WebhookEndpointHealthEvent>().Any());
        var evt = spy.Received.OfType<WebhookEndpointHealthEvent>().ShouldHaveSingleItem();
        evt.Type.ShouldBe(WarpEventType.WebhookEndpointFailing);
        evt.GroupName.ShouldBe(Group);
    }

    [TimedFact]
    public async Task Send_WhileGroupFailing_DeliveryParksInsteadOfExhausting()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        await using var server = await StartServerAsync(receiver);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);

        var second = await SendAsync(server, "second");
        await WaitForStatusAsync(server, second, WebhookDeliveryStatus.Parked);

        var parked = await GetDeliveryAsync(second);
        parked.NextAttemptAt.ShouldBeNull();
        parked.ExhaustedCallbackPending.ShouldBeFalse();
        parked.LastStatusCode.ShouldBe(500);
        parked.LastFailureReason.ShouldBe(WebhookFailureReason.UnsuccessfulStatus);
        (await GetEndpointAsync(Group)).State.ShouldBe(WebhookEndpointState.Failing);
    }

    [TimedFact]
    public async Task Send_SuccessWhileFailing_RecoversGroupAndMonitorReleasesParked()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        var health = new EndpointHealthRecorder();
        await using var server = await StartServerAsync(receiver, health: health);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);
        var parked = await SendAsync(server, "parked");
        await WaitForStatusAsync(server, parked, WebhookDeliveryStatus.Parked);

        receiver.Status = HttpStatusCode.OK;
        var third = await SendAsync(server, "third");
        await WaitForStatusAsync(server, third, WebhookDeliveryStatus.Delivered);

        var endpoint = await GetEndpointAsync(Group);
        endpoint.State.ShouldBe(WebhookEndpointState.Healthy);
        endpoint.FailingSince.ShouldBeNull();
        endpoint.NextProbeAt.ShouldBeNull();
        endpoint.LastSuccessAt.ShouldNotBeNull();
        await WaitUntilAsync(() => health.Kinds.Contains("recovered"));

        // Recovery itself releases nothing; the monitor drains what parked.
        (await GetDeliveryAsync(parked)).Status.ShouldBe(WebhookDeliveryStatus.Parked);

        (await CreateMonitor().ExecuteAsync(Ct)).ShouldNotBeNull().ShouldStartWith("Released 1 ");

        await WaitForStatusAsync(server, parked, WebhookDeliveryStatus.Delivered);
        (await GetDeliveryAsync(parked)).AttemptCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task Monitor_ProbeWhileReceiverStillDown_ParksAgainAndKeepsFailing()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        await using var server = await StartServerAsync(receiver);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);
        var parked = await SendAsync(server, "parked");
        await WaitForStatusAsync(server, parked, WebhookDeliveryStatus.Parked);
        await MakeProbeDueAsync();

        var result = await CreateMonitor().ExecuteAsync(Ct);

        result.ShouldNotBeNull().ShouldContain("probed 1");
        await WaitUntilAsync(async () => (await GetDeliveryAsync(parked)) is { Status: WebhookDeliveryStatus.Parked, AttemptCount: 1 } row
            && row.LastAttemptAt is not null
            && receiver.CountFor("parked") == 2);

        var endpoint = await GetEndpointAsync(Group);
        endpoint.State.ShouldBe(WebhookEndpointState.Failing);
        endpoint.NextProbeAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task Monitor_ProbeSucceeds_RecoversGroupAndNextRunReleasesTheRest()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        await using var server = await StartServerAsync(receiver);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);
        var oldest = await SendAsync(server, "oldest");
        await WaitForStatusAsync(server, oldest, WebhookDeliveryStatus.Parked);
        var newer = await SendAsync(server, "newer");
        await WaitForStatusAsync(server, newer, WebhookDeliveryStatus.Parked);

        receiver.Status = HttpStatusCode.OK;
        await MakeProbeDueAsync();
        await CreateMonitor().ExecuteAsync(Ct);

        // The probe re-sends the OLDEST parked delivery only.
        await WaitForStatusAsync(server, oldest, WebhookDeliveryStatus.Delivered);
        (await GetDeliveryAsync(newer)).Status.ShouldBe(WebhookDeliveryStatus.Parked);
        (await GetEndpointAsync(Group)).State.ShouldBe(WebhookEndpointState.Healthy);

        await CreateMonitor().ExecuteAsync(Ct);

        await WaitForStatusAsync(server, newer, WebhookDeliveryStatus.Delivered);
    }

    [TimedFact]
    public async Task Send_SuccessInAnotherGroupWithTheSameUrl_DoesNotRecoverTheGroup()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        await using var server = await StartServerAsync(receiver);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);

        receiver.Status = HttpStatusCode.OK;
        var other = await SendAsync(server, "other", group: "globex");
        await WaitForStatusAsync(server, other, WebhookDeliveryStatus.Delivered);

        (await GetEndpointAsync(Group)).State.ShouldBe(WebhookEndpointState.Failing);
    }

    [TimedFact]
    public async Task Send_WithoutTracking_ExhaustsAndRecordsNoEndpoint()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.InternalServerError);
        await using var server = await StartServerAsync(receiver, trackEndpointHealth: false);

        var first = await SendAsync(server, "first");
        await WaitForStatusAsync(server, first, WebhookDeliveryStatus.Exhausted);
        var second = await SendAsync(server, "second");
        await WaitForStatusAsync(server, second, WebhookDeliveryStatus.Exhausted);

        (await GetDeliveryAsync(second)).TrackEndpointHealth.ShouldBeFalse();
        (await Fixture.CreateContext().Set<WebhookEndpoint>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Monitor_FailingPastTheThreshold_SignalsStillFailingOncePerPeriod()
    {
        var failingSince = DateTime.UtcNow.AddDays(-31);
        await SeedEndpointAsync(WebhookEndpointState.Failing, failingSince, nextProbeAt: DateTime.UtcNow.AddHours(1));
        var spy = new SpyNotifier();
        var health = new EndpointHealthRecorder();

        await CreateMonitor(spy, health).ExecuteAsync(Ct);
        await CreateMonitor(spy, health).ExecuteAsync(Ct);

        health.Kinds.ShouldBe(["still-failing"]);
        var evt = spy.Received.OfType<WebhookEndpointHealthEvent>().ShouldHaveSingleItem();
        evt.Type.ShouldBe(WarpEventType.WebhookEndpointStillFailing);
        evt.FailingSince.ShouldNotBeNull();
        (await GetEndpointAsync(Group)).StillFailingNotifiedAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Monitor_ReleaseRecovered_StagesJobsOldestFirstWithAFreshBudget()
    {
        await SeedEndpointAsync(WebhookEndpointState.Healthy, failingSince: null, nextProbeAt: null);
        var now = DateTime.UtcNow;
        var newest = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, now.AddMinutes(-1), attemptCount: 3);
        var oldest = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, now.AddMinutes(-3), attemptCount: 3);
        var middle = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, now.AddMinutes(-2), attemptCount: 3);

        var result = await CreateMonitor().ExecuteAsync(Ct);

        result.ShouldNotBeNull().ShouldStartWith("Released 3 ");
        var jobs = await Fixture.CreateContext().Set<Job>()
            .AsNoTracking()
            .Where(x => x.Queue == WebhookConstants.Queue)
            .OrderBy(x => x.ScheduleTime)
            .Select(x => x.Message)
            .ToListAsync(Ct);

        jobs.Count.ShouldBe(3);
        jobs[0]!.ShouldContain(oldest.ToString());
        jobs[1]!.ShouldContain(middle.ToString());
        jobs[2]!.ShouldContain(newest.ToString());

        var rows = await Fixture.CreateContext().Set<WebhookDelivery>().AsNoTracking().ToListAsync(Ct);
        rows.ShouldAllBe(x => x.Status == WebhookDeliveryStatus.Pending && x.AttemptCount == 0);
    }

    [TimedFact]
    public async Task ProbeEndpoint_FailingGroupWithParked_ReleasesTheOldestForOneAttempt()
    {
        await SeedEndpointAsync(WebhookEndpointState.Failing, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(30));
        var oldest = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, DateTime.UtcNow.AddMinutes(-5), attemptCount: 3);
        var newer = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, DateTime.UtcNow.AddMinutes(-4), attemptCount: 3);

        var result = await CreateCommandService().ProbeEndpoint(Group, Ct);

        result.ShouldBe(WebhookProbeResult.Probing);
        var probed = await GetDeliveryAsync(oldest);
        probed.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        probed.AttemptCount.ShouldBe(2); // the schedule has 2 entries: one attempt left
        (await GetDeliveryAsync(newer)).Status.ShouldBe(WebhookDeliveryStatus.Parked);
    }

    [TimedTheory]
    [InlineData(null, WebhookProbeResult.NotFound)]
    [InlineData(WebhookEndpointState.Healthy, WebhookProbeResult.NotFailing)]
    [InlineData(WebhookEndpointState.Failing, WebhookProbeResult.NothingParked)]
    public async Task ProbeEndpoint_NothingToProbe_ReportsWhy(WebhookEndpointState? state, WebhookProbeResult expected)
    {
        if (state is { } s)
        {
            await SeedEndpointAsync(s, s == WebhookEndpointState.Failing ? DateTime.UtcNow : null, null);
        }

        var result = await CreateCommandService().ProbeEndpoint(Group, Ct);

        result.ShouldBe(expected);
    }

    private WebhookEndpointMonitor<TestContext> CreateMonitor(SpyNotifier? spy = null, EndpointHealthRecorder? health = null)
        => new(
            new TestServerContext(Fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            TimeProvider.System,
            spy is null ? TestNotifiers.EmptyDispatcher() : TestNotifiers.SpyDispatcher(spy),
            health is null ? [] : [new RecordingEndpointHealthHandler(health)],
            TestTasks.NullTransport,
            TestTasks.NullSignals,
            NullLogger<WebhookEndpointMonitor<TestContext>>.Instance);

    private WebhookCommandService<TestContext> CreateCommandService()
        => new(Fixture.CreateContext(), TimeProvider.System, Options.Create(new WarpConfiguration()), [new NoOpRedeliveryEnqueuer()]);

    private Task<WarpTestServer> StartServerAsync(
        SwitchableReceiver receiver,
        SpyNotifier? spy = null,
        EndpointHealthRecorder? health = null,
        bool trackEndpointHealth = true)
    {
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w =>
                {
                    w.TrackEndpointHealth = trackEndpointHealth;
                    w.OnEndpointHealthChanged<RecordingEndpointHealthHandler>();
                });

                // The monitor is driven by the tests; the sweeps stay away from the hand-made states.
                cfg.WebhookEndpointMonitorInterval = null;
                cfg.StaleJobRecoveryInterval = null;
                cfg.ExpirationCleanupInterval = null;
            },
            configureServices: services =>
            {
                services.AddSingleton(health ?? new EndpointHealthRecorder());
                if (spy is not null)
                {
                    services.AddSingleton<IWarpNotifier>(spy);
                }

                services.AddHttpClient(WebhookConstants.AdapterName)
                    .ConfigurePrimaryHttpMessageHandler(() => receiver);
            });
    }

    private static async Task<Guid> SendAsync(WarpTestServer server, string payload, IReadOnlyList<TimeSpan>? retrySchedule = null, string group = Group)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>().SendAsync(
            new WebhookSend
            {
                Url = Url,
                EventType = "order.created",
                Group = group,
                Payload = payload,
                RetrySchedule = retrySchedule ?? [],
            },
            Ct);
    }

    private async Task MakeProbeDueAsync()
        => await Fixture.CreateContext().Set<WebhookEndpoint>()
            .Where(x => x.Name == Group)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextProbeAt, DateTime.UtcNow.AddMinutes(-1)), Ct);

    private async Task SeedEndpointAsync(WebhookEndpointState state, DateTime? failingSince, DateTime? nextProbeAt)
    {
        var ctx = Fixture.CreateContext();
        ctx.Set<WebhookEndpoint>().Add(new WebhookEndpoint
        {
            Name = Group,
            State = state,
            FailingSince = failingSince,
            NextProbeAt = nextProbeAt,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedDeliveryAsync(WebhookDeliveryStatus status, DateTime createdAt, int attemptCount)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = Url,
            GroupName = Group,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)],
            Status = status,
            AttemptCount = attemptCount,
            TrackEndpointHealth = true,
            CreatedAt = createdAt,
        };

        var ctx = Fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private async Task<WebhookDelivery> GetDeliveryAsync(Guid id)
        => await Fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);

    private async Task<WebhookEndpoint> GetEndpointAsync(string name)
        => await Fixture.CreateContext().Set<WebhookEndpoint>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .FirstAsync(Ct);

    private static Task WaitForStatusAsync(WarpTestServer server, Guid id, WebhookDeliveryStatus status)
        => WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == status,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);

    private static Task WaitUntilAsync(Func<bool> condition)
        => WarpTestServer.WaitUntil(() => Task.FromResult(condition()), timeout: TimeSpan.FromSeconds(5), ct: Ct);

    private static Task WaitUntilAsync(Func<Task<bool>> condition)
        => WarpTestServer.WaitUntil(condition, timeout: TimeSpan.FromSeconds(8), ct: Ct);
}

/// <summary>A stub receiver whose response status the test flips; records each request's payload.</summary>
internal sealed class SwitchableReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _payloads = new();
    private int _status;

    public SwitchableReceiver(HttpStatusCode status) => _status = (int)status;

    public HttpStatusCode Status
    {
        get => (HttpStatusCode)Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, (int)value);
    }

    public int CountFor(string payload) => _payloads.Count(x => string.Equals(x, payload, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _payloads.Enqueue(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        return new HttpResponseMessage(Status) { Content = new StringContent("ok") };
    }
}

/// <summary>Records the endpoint health callbacks in the order they arrive.</summary>
internal sealed class EndpointHealthRecorder
{
    private readonly ConcurrentQueue<(string Kind, WebhookEndpointHealthChange Change)> _calls = new();

    public int Count => _calls.Count;

    public IReadOnlyList<string> Kinds => [.. _calls.Select(x => x.Kind)];

    public void Add(string kind, WebhookEndpointHealthChange change) => _calls.Enqueue((kind, change));
}

internal sealed class RecordingEndpointHealthHandler : IWebhookEndpointHealthHandler
{
    private readonly EndpointHealthRecorder _recorder;

    public RecordingEndpointHealthHandler(EndpointHealthRecorder recorder) => _recorder = recorder;

    public Task OnEndpointFailingAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken)
    {
        _recorder.Add("failing", change);

        return Task.CompletedTask;
    }

    public Task OnEndpointRecoveredAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken)
    {
        _recorder.Add("recovered", change);

        return Task.CompletedTask;
    }

    public Task OnEndpointStillFailingAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken)
    {
        _recorder.Add("still-failing", change);

        return Task.CompletedTask;
    }
}
