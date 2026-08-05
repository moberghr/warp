using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 11 BACKWARD-COMPAT / migration-additivity coverage for multi-application observability (§8.19, §6
/// "100% additive"). Legacy / pre-upgrade rows have <c>Application == null</c>. These tests prove: (a) the new
/// columns are nullable — every producer row inserts WITHOUT an <c>Application</c> and reads back cleanly;
/// (b) the two new tables are in the model (the additive schema delta EF picks up); and (c) the query
/// services never choke on null <c>Application</c> — a null-app job is surfaced in the app-agnostic listing
/// but excluded when filtered by a real app, and the application roster / per-app readers only ever include
/// instances / metrics whose <c>Application</c> is set (a null-app row is the "(unassigned)" bucket the
/// dashboard renders, never a roster entry). Plain (light) — direct inserts, no host. Fresh context per phase
/// (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class BackwardCompatTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    protected BackwardCompatTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task NewTables_ArePresentInModel()
    {
        // The additive schema delta: the two new entities are contributed unconditionally (§2.11), so a
        // user's `dotnet ef migrations add` sees them. Model presence is the additivity contract.
        await using var ctx = _fixture.CreateContext();

        ctx.Model.FindEntityType(typeof(ApplicationInstance)).ShouldNotBeNull();
        ctx.Model.FindEntityType(typeof(ApplicationInstanceLog)).ShouldNotBeNull();
    }

    [TimedFact]
    public async Task LegacyRows_InsertWithoutApplication_AndReadBackNull()
    {
        // Additive columns are nullable: a row that never sets Application (old-version writer / pre-upgrade
        // legacy row) inserts with no NOT-NULL violation and reads back null on every producer surface.
        var jobId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        var arrange = _fixture.CreateContext();
        arrange.Set<Job>().Add(new Job
        {
            Id = jobId,
            Type = "LegacyJob",
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        arrange.Set<Server>().Add(new Server
        {
            Id = serverId,
            ServerName = "legacy-host",
            StartedTime = DateTime.UtcNow.AddHours(-1),
            LastHeartbeatTime = DateTime.UtcNow,
        });
        arrange.Set<AdapterCallLog>().Add(new AdapterCallLog
        {
            AdapterName = "legacy-vendor",
            Operation = "GetThing",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "legacy-host",
        });
        arrange.Set<EndpointCallLog>().Add(new EndpointCallLog
        {
            Method = "GET",
            RouteTemplate = "/legacy",
            Operation = "Legacy",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "legacy-host",
        });
        arrange.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = "evt-legacy",
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Delivered,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });

        await arrange.SaveChangesAsync(Ct);

        await using var read = _fixture.CreateContext();
        (await read.Set<Job>().SingleAsync(x => x.Id == jobId, Ct)).Application.ShouldBeNull();
        (await read.Set<Server>().SingleAsync(x => x.Id == serverId, Ct)).Application.ShouldBeNull();
        (await read.Set<AdapterCallLog>().SingleAsync(Ct)).Application.ShouldBeNull();
        (await read.Set<EndpointCallLog>().SingleAsync(Ct)).Application.ShouldBeNull();
        (await read.Set<WebhookDelivery>().SingleAsync(x => x.Id == deliveryId, Ct)).Application.ShouldBeNull();
    }

    [TimedFact]
    public async Task NullApplicationServer_IsExcludedFromApplicationRoster()
    {
        // The roster is opt-in: an instance whose Application is null (a legacy server, or any server that
        // never set ApplicationName) never appears — the query service must not surface it or throw.
        var arrange = _fixture.CreateContext();
        arrange.Set<Server>().Add(new Server
        {
            Id = Guid.NewGuid(),
            ServerName = "legacy-host",
            StartedTime = DateTime.UtcNow.AddHours(-1),
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await arrange.SaveChangesAsync(Ct);

        var roster = await CreateApplicationQuery().GetApplications(Ct);

        roster.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task NullApplicationRows_ProduceNoPerAppAdapterOrEndpointMetrics()
    {
        // Raw call logs with a null Application carry no per-app counter keys, so the per-app readers report
        // no applications — the metrics side degrades to empty rather than mis-attributing to a bucket.
        var arrange = _fixture.CreateContext();
        arrange.Set<AdapterCallLog>().Add(new AdapterCallLog
        {
            AdapterName = "legacy-vendor",
            Operation = "GetThing",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "legacy-host",
        });
        arrange.Set<EndpointCallLog>().Add(new EndpointCallLog
        {
            Method = "GET",
            RouteTemplate = "/legacy",
            Operation = "Legacy",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "legacy-host",
        });
        await arrange.SaveChangesAsync(Ct);

        (await new AdapterQueryService<TestContext>(_fixture.CreateContext(), new LocalMetricSource<TestContext>(_fixture.CreateContext())).GetApplications(Ct)).ShouldBeEmpty();
        (await new EndpointQueryService<TestContext>(_fixture.CreateContext()).GetApplications(Ct)).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task NullApplicationJob_SurfacedInAppAgnosticList_ExcludedWhenFilteredByApp()
    {
        var jobId = Guid.NewGuid();
        var arrange = _fixture.CreateContext();
        arrange.Set<Job>().Add(new Job
        {
            Id = jobId,
            Type = "LegacyJob",
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await arrange.SaveChangesAsync(Ct);

        var query = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);

        // App-agnostic listing (no filter) surfaces the null-app job — it is NOT hidden.
        var unfiltered = await query.GetJobsList(new BaseListRequest(), State.Completed, application: null);
        unfiltered.Items.ShouldHaveSingleItem().Id.ShouldBe(jobId);

        // Filtering by a real application excludes the null-app (unassigned) job.
        var filtered = await new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System)
            .GetJobsList(new BaseListRequest(), State.Completed, "some-app");
        filtered.Items.ShouldBeEmpty();
    }

    private ApplicationQueryService<TestContext> CreateApplicationQuery()
        => new(
            _fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration { ApplicationInstanceStaleGrace = TimeSpan.FromMinutes(2) }));
}
