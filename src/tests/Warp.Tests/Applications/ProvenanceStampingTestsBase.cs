using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 4 coverage for multi-application observability provenance: the current process's
/// <see cref="WarpConfiguration.ApplicationName"/> is stamped onto every producer surface
/// (<see cref="Job"/> at publish, <see cref="AdapterCallLog"/>, <see cref="EndpointCallLog"/>,
/// <see cref="WebhookDelivery"/>) when set, stays null when unset, and is preserved across
/// <c>RequeueJob</c>. Each test drives exactly one public method (§4.8).
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class ProvenanceStampingTestsBase : IAsyncLifetime
{
    private const string AppName = "reporting-api";

    private readonly IDatabaseFixture _fixture;

    protected ProvenanceStampingTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Enqueue_ApplicationNameSet_StampsJobApplication()
    {
        var id = await PublishJobAsync(AppName);

        var job = await _fixture.CreateContext().Set<Job>().SingleAsync(x => x.Id == id, Ct);
        job.Application.ShouldBe(AppName);
    }

    [TimedFact]
    public async Task Enqueue_ApplicationNameUnset_LeavesJobApplicationNull()
    {
        var id = await PublishJobAsync(application: null);

        var job = await _fixture.CreateContext().Set<Job>().SingleAsync(x => x.Id == id, Ct);
        job.Application.ShouldBeNull();
    }

    [TimedFact]
    public async Task StartNewBatch_ApplicationNameSet_StampsParentAndChildren()
    {
        // BatchPublisher stamps Application on the batch parent AND each child — a separate call site from
        // Publisher, so it needs its own provenance coverage.
        var parentId = await PublishBatchAsync(AppName);

        var ctx = _fixture.CreateContext();
        var parent = await ctx.Set<Job>().SingleAsync(x => x.Id == parentId, Ct);
        parent.Application.ShouldBe(AppName);

        var children = await ctx.Set<Job>()
            .Where(x => x.ParentJobId == parentId)
            .ToListAsync(Ct);

        children.Count.ShouldBe(2);
        children.ShouldAllBe(x => string.Equals(x.Application, AppName, StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task StartNewBatch_ApplicationNameUnset_LeavesParentAndChildrenNull()
    {
        var parentId = await PublishBatchAsync(application: null);

        var ctx = _fixture.CreateContext();
        var parent = await ctx.Set<Job>().SingleAsync(x => x.Id == parentId, Ct);
        parent.Application.ShouldBeNull();

        var children = await ctx.Set<Job>()
            .Where(x => x.ParentJobId == parentId)
            .ToListAsync(Ct);

        children.Count.ShouldBe(2);
        children.ShouldAllBe(x => x.Application == null);
    }

    [TimedFact]
    public async Task RequeueJob_PreservesJobApplication()
    {
        var jobId = Guid.NewGuid();
        var setup = _fixture.CreateContext();
        setup.Set<Job>().Add(new Job
        {
            Id = jobId,
            Type = "T",
            Application = AppName,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await setup.SaveChangesAsync(Ct);

        var svc = TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        var job = await _fixture.CreateContext().Set<Job>().SingleAsync(x => x.Id == jobId, Ct);
        job.CurrentState.ShouldBe(State.Enqueued);
        job.Application.ShouldBe(AppName);
    }

    [TimedFact]
    public async Task PersistBatch_ApplicationNameSet_StampsAdapterCallLog()
    {
        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetThing",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            [record],
            new AdapterRegistry(),
            new WarpConfiguration { ApplicationName = AppName },
            TimeProvider.System,
            Ct);

        var row = await _fixture.CreateContext().Set<AdapterCallLog>().SingleAsync(Ct);
        row.Application.ShouldBe(AppName);
    }

    [TimedFact]
    public async Task PersistBatch_ApplicationNameSet_StampsEndpointCallLog()
    {
        var record = new EndpointCallRecord
        {
            Method = "GET",
            RouteTemplate = "/things/{id}",
            Operation = "GetThing",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            [record],
            new WarpConfiguration { ApplicationName = AppName },
            TimeProvider.System,
            Ct);

        var row = await _fixture.CreateContext().Set<EndpointCallLog>().SingleAsync(Ct);
        row.Application.ShouldBe(AppName);
    }

    [TimedFact]
    public async Task SendAsync_ApplicationNameSet_StampsWebhookDelivery()
    {
        var configuration = Options.Create(new WarpConfiguration { ApplicationName = AppName });
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(
            ctx,
            configuration,
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            TestTasks.NullTransport,
            TestTasks.NullSignals);
        var dispatcher = new WebhookDispatcher<TestContext>(ctx, publisher, TimeProvider.System, configuration);

        var id = await dispatcher.SendAsync(
            new WebhookSend { Url = "https://example.test/hook", EventType = "order.created" },
            Ct);

        var row = await _fixture.CreateContext().Set<WebhookDelivery>().SingleAsync(x => x.Id == id, Ct);
        row.Application.ShouldBe(AppName);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<Guid> PublishJobAsync(string? application)
    {
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(
            ctx,
            Options.Create(new WarpConfiguration { ApplicationName = application }),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            TestTasks.NullTransport,
            TestTasks.NullSignals);

        var id = await publisher.Enqueue(new ProvenanceJob());
        await publisher.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<Guid> PublishBatchAsync(string? application)
    {
        var ctx = _fixture.CreateContext();
        var batchPublisher = new BatchPublisher<TestContext>(
            ctx,
            Options.Create(new WarpConfiguration { ApplicationName = application }),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            TestTasks.NullTransport,
            TestTasks.NullSignals);

        var parentId = await batchPublisher.StartNew(new List<ProvenanceJob> { new(), new() });
        await batchPublisher.SaveChangesAsync(Ct);

        return parentId;
    }

    private sealed record ProvenanceJob : IJob;
}
