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
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 11 cross-cutting END-TO-END coverage for multi-application observability (§8.19): two applications
/// coexisting on ONE shared schema (one fixture / one container), asserted through the unified reader stack
/// (<see cref="IApplicationQueryService"/> + the per-app adapter / endpoint / job readers).
/// <para>
/// <b>Chosen approach — one real server + a non-server publisher (NOT two full hosts).</b> A single
/// <see cref="WarpTestServer"/> runs as application <c>app-a</c> (the executor: worker + server tasks,
/// <c>WorkerCount=1</c> — good neighbour §4.7.1, and this class joins the serialized HeavyIntegration
/// collection). <c>app-b</c> is a non-server publisher: its instance is an <c>ApplicationInstance</c> row and
/// its jobs are staged by a manually-constructed <see cref="Publisher{TContext}"/> carrying
/// <c>ApplicationName = app-b</c>. This is the plan's explicitly-permitted "single server plus two publishers
/// with different ApplicationName" shape — a second full host doubles the worker/server-task fleet hammering
/// the shared container for no extra coverage of the thing under test (the READ path unifying two apps'
/// provenance + metrics without key collision or cross-attribution). Adapter / endpoint per-app traffic is
/// driven straight through the flushers under each app's config (the real persistence path), then folded by
/// <c>CounterAggregator</c> and read back per app. The point being verified: on one schema, provenance is
/// attributed to the CREATOR, execution metrics to the EXECUTOR, and every per-app key stays disjoint.
/// </para>
/// Fresh context per arrange/act/assert phase (§4.8); each test drives one public reader entry point.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class MultiAppEndToEndTestsBase : IntegrationTestBase
{
    private const string AppA = "app-a";
    private const string AppB = "app-b";
    private const string Adapter = "vendor";
    private const string RouteTemplate = "/orders/{id:int}";

    private static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(2);
    private static readonly string TypeUnit = typeof(UnitRequest).AssemblyQualifiedName!;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    protected MultiAppEndToEndTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Roster_UnifiesServerAndNonServerInstances_AcrossBothApps()
    {
        // app-b registers as a non-server instance; app-a is the running server (its Server row is stamped
        // by WarpServerRegistration when it starts).
        await InsertNonServerInstanceAsync(AppB, version: "2.0.0", environment: "staging");

        await using var server = await StartExecutorAsync();

        var roster = await CreateApplicationQuery().GetApplications(Ct);

        var appA = roster.Where(x => string.Equals(x.Name, AppA, StringComparison.Ordinal)).ShouldHaveSingleItem();
        appA.InstanceCount.ShouldBe(1);
        appA.LiveInstanceCount.ShouldBe(1);

        var appB = roster.Where(x => string.Equals(x.Name, AppB, StringComparison.Ordinal)).ShouldHaveSingleItem();
        appB.InstanceCount.ShouldBe(1);
        appB.LiveInstanceCount.ShouldBe(1);
        appB.Versions.ShouldBe(["2.0.0"]);
        appB.Environments.ShouldBe(["staging"]);

        // The unified detail projection distinguishes the server (app-a) from the non-server instance (app-b).
        var detailA = await CreateApplicationQuery().GetApplicationDetail(AppA, Ct);
        detailA.ShouldNotBeNull();
        detailA!.Instances.ShouldHaveSingleItem().IsServer.ShouldBeTrue();

        var detailB = await CreateApplicationQuery().GetApplicationDetail(AppB, Ct);
        detailB.ShouldNotBeNull();
        detailB!.Instances.ShouldHaveSingleItem().IsServer.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Jobs_ProvenanceIsCreator_ExecutionIsExecutor_AndFilterable()
    {
        await using var server = await StartExecutorAsync();

        // app-a (the server) publishes one job; app-b (a non-server publisher) publishes another. Both land
        // on the shared queue and are executed by the single worker (app-a) — routing is unchanged.
        var idA = await PublishAsServerAsync(server);
        var idB = await PublishAsNonServerAsync(AppB);

        await server.WaitForCompletion();

        // ---- provenance: each job carries its CREATOR's application; both still executed ----
        await using (var ctx = Fixture.CreateContext())
        {
            var jobA = await ctx.Set<Job>().AsNoTracking().SingleAsync(x => x.Id == idA, Ct);
            jobA.Application.ShouldBe(AppA);
            jobA.CurrentState.ShouldBe(State.Completed);

            var jobB = await ctx.Set<Job>().AsNoTracking().SingleAsync(x => x.Id == idB, Ct);
            jobB.Application.ShouldBe(AppB);
            jobB.CurrentState.ShouldBe(State.Completed);
        }

        // ---- filtering by application returns only that app's rows ----
        var onlyA = await CreateJobQuery().GetJobsList(new BaseListRequest(), State.Completed, AppA);
        onlyA.Items.ShouldHaveSingleItem().Id.ShouldBe(idA);

        var onlyB = await CreateJobQuery().GetJobsList(new BaseListRequest(), State.Completed, AppB);
        onlyB.Items.ShouldHaveSingleItem().Id.ShouldBe(idB);

        var unfiltered = await CreateJobQuery().GetJobsList(new BaseListRequest(), State.Completed, application: null);
        unfiltered.Items.Select(x => x.Id).ShouldBe([idA, idB], ignoreOrder: true);

        // ---- execution metrics attribute to the EXECUTOR (app-a ran both jobs), never the creator ----
        await AggregateAsync();

        var executorMetrics = await CreateJobQuery().GetJobExecutionMetrics(AppA);
        executorMetrics.ByType
            .Where(x => string.Equals(x.Identifier, TypeUnit, StringComparison.Ordinal))
            .ShouldHaveSingleItem()
            .ExecutedCount.ShouldBe(2);

        // app-b created a job but executed nothing — it has no execution slice.
        var creatorMetrics = await CreateJobQuery().GetJobExecutionMetrics(AppB);
        creatorMetrics.ByType.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task AdapterAndEndpointMetrics_AreKeyedToTheProducingApp_AndStayDisjoint()
    {
        // Same adapter name and same route under BOTH apps — the strongest disjointness proof: the per-app
        // key namespace must keep the two applications' aggregates separate rather than summing them.
        await PersistAdapterAsync(AppA, AdapterCallOutcome.Success, AdapterCallOutcome.Success);
        await PersistAdapterAsync(AppB, AdapterCallOutcome.Success);

        await PersistEndpointAsync(AppA, AdapterCallOutcome.Success, AdapterCallOutcome.Success);
        await PersistEndpointAsync(AppB, AdapterCallOutcome.Success);

        await AggregateAsync();

        // ---- adapters: distinct applications, each with its own call count ----
        var adapterQuery = new AdapterQueryService<TestContext>(Fixture.CreateContext(), new LocalMetricSource<TestContext>(Fixture.CreateContext()));
        (await adapterQuery.GetApplications(Ct)).ShouldBe([AppA, AppB]);

        var adapterA = (await adapterQuery.GetAdapterStatsByApplication(AppA, Ct)).ShouldHaveSingleItem();
        adapterA.Application.ShouldBe(AppA);
        adapterA.Adapter.ShouldBe(Adapter);
        adapterA.Calls.ShouldBe(2);

        var adapterB = (await adapterQuery.GetAdapterStatsByApplication(AppB, Ct)).ShouldHaveSingleItem();
        adapterB.Application.ShouldBe(AppB);
        adapterB.Adapter.ShouldBe(Adapter);
        adapterB.Calls.ShouldBe(1);

        // ---- endpoints: same route, application is part of identity → two distinct aggregates ----
        var endpointQuery = new EndpointQueryService<TestContext>(Fixture.CreateContext(), new LocalMetricSource<TestContext>(Fixture.CreateContext()));
        (await endpointQuery.GetApplications(Ct)).ShouldBe([AppA, AppB]);

        var endpointA = (await endpointQuery.GetEndpointStatsByApplication(AppA, Ct)).ShouldHaveSingleItem();
        endpointA.Application.ShouldBe(AppA);
        endpointA.Route.ShouldBe("GET /orders/{id}");
        endpointA.Calls.ShouldBe(2);

        var endpointB = (await endpointQuery.GetEndpointStatsByApplication(AppB, Ct)).ShouldHaveSingleItem();
        endpointB.Application.ShouldBe(AppB);
        endpointB.Route.ShouldBe("GET /orders/{id}");
        endpointB.Calls.ShouldBe(1);
    }

    private async Task<WarpTestServer> StartExecutorAsync()
    {
        return await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            // Good neighbour (§4.7.1): one worker on the shared container; this class is already serialized.
            cfg.WorkerCount = 1;
            cfg.ApplicationName = AppA;
            cfg.ApplicationVersion = "1.0.0";
            cfg.ApplicationEnvironment = "prod";
        });
    }

    private static async Task<Guid> PublishAsServerAsync(WarpTestServer server)
    {
        // The server's publisher carries the server's ApplicationName (app-a) — provenance for a job created
        // by the server process itself.
        var publisher = server.CreatePublisher();
        var id = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<Guid> PublishAsNonServerAsync(string application)
    {
        var publisher = new Publisher<TestContext>(
            Fixture.CreateContext(),
            Options.Create(new WarpConfiguration { ApplicationName = application }),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            TestTasks.NullTransport,
            TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Ct);

        return id;
    }

    private async Task PersistAdapterAsync(string application, params AdapterCallOutcome[] outcomes)
    {
        var records = outcomes
            .Select(x =>
                new AdapterCallRecord
                {
                    AdapterName = Adapter,
                    Operation = "GetOrders",
                    Timestamp = DateTime.UtcNow,
                    DurationMs = 5,
                    Attempts = 1,
                    Outcome = x,
                    MachineName = "test-host",
                })
            .ToArray();

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            Fixture.CreateContext(),
            records,
            new AdapterRegistry(),
            new WarpConfiguration { ApplicationName = application },
            TimeProvider.System,
            Ct);
    }

    private async Task PersistEndpointAsync(string application, params AdapterCallOutcome[] outcomes)
    {
        var records = outcomes
            .Select(x =>
                new EndpointCallRecord
                {
                    Method = "GET",
                    RouteTemplate = RouteTemplate,
                    Operation = "GetOrders",
                    Timestamp = DateTime.UtcNow,
                    DurationMs = 5,
                    Outcome = x,
                    MachineName = "test-host",
                })
            .ToArray();

        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            Fixture.CreateContext(),
            records,
            new WarpConfiguration { ApplicationName = application },
            TimeProvider.System,
            Ct);
    }

    private async Task InsertNonServerInstanceAsync(string application, string version, string environment)
    {
        var ctx = Fixture.CreateContext();
        ctx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = Guid.NewGuid(),
            ApplicationName = application,
            MachineName = "publisher-host",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTime.UtcNow,
            CpuUsagePercent = 3,
            MemoryWorkingSetBytes = 300,
            Version = version,
            Environment = environment,
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private async Task AggregateAsync()
        => await TestTasks.CreateCounterAggregator(Fixture.CreateContext()).AggregateCountersAsync(Ct);

    private ApplicationQueryService<TestContext> CreateApplicationQuery()
        => new(
            Fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration { ApplicationInstanceStaleGrace = StaleGrace }));

    private JobQueryService<TestContext> CreateJobQuery()
        => new(Fixture.CreateContext(), TimeProvider.System, new LocalMetricSource<TestContext>(Fixture.CreateContext()));
}
