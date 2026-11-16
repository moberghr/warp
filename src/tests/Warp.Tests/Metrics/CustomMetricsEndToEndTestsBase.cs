using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// AC1 end to end (SC1, SC5, SC6/R14) on the DEFAULT registration, in the deployment shape the docs recommend
/// beside another job system: an <c>AddWarp</c>-only web app collects its own meter and ASP.NET Core's built-in
/// <c>http.server.request.duration</c>, and a separate <c>AddWarpServer(o =&gt; o.DisableWorker())</c> compacts and
/// rolls those rows up, evaluates metric SLOs, and claims no job.
/// <para>
/// One <see cref="FakeTimeProvider"/> drives both hosts, so every measurement lands in one known fine bucket, the
/// emitter's periodic flush never fires (only the graceful stop writes, which is what makes "the flusher, not the
/// request, wrote the rows" observable), and the rollup is moved across the closed-bucket and fine-retention edges
/// without a real wait. <c>MeterListener</c> is process-global, so the meter name is unique per run and every
/// ASP.NET Core assertion is filtered to this test's route: another test's TestServer requests reach this
/// collector too (the hosting meter is global), always under a different route.
/// </para>
/// <para>
/// Deliberately ONE test with labelled <c>// Assert (a)…(d)</c> sections rather than four: every outcome needs the
/// same two booted hosts and the same flush, so splitting would quadruple the heavy host boots. There is no
/// <c>HttpClient</c> (<c>http.client.request.duration</c>) leg: <c>System.Net.Http</c> records that instrument in
/// the <c>SocketsHttpHandler</c>/<c>HttpClientHandler</c> pipeline, and a client aimed at the in-memory TestServer
/// runs on TestServer's own handler, so it would record nothing. The preset itself is pinned by
/// <c>WarpMetricsOptionsTests</c>.
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class CustomMetricsEndToEndTestsBase : IntegrationTestBase
{
    private const string Application = "emitter";
    private const string PaymentResults = "payment.results";
    private const string RouteTemplate = "/api/adyen/{id}";
    private const string RouteTag = "http.route=" + RouteTemplate;

    // 10:01 — inside the 10:00 fine bucket, far from its edges.
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 1, 0, TimeSpan.Zero);
    private static readonly DateTime Bucket = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Traffic = ["success", "success", "refused", "error", "success", "error"];

    private static readonly string[] AspNetCoreTagKeys = ["http.request.method", "http.response.status_code", "http.route"];

    protected CustomMetricsEndToEndTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task AddWarpEmitter_AndServiceOnlyServer_RollUpEvaluateAndClaimNothing()
    {
        var time = new FakeTimeProvider(Start);
        var (connectionString, isPostgres) = await ProbeAsync();
        var jobId = await SeedEnqueuedJobAsync();
        var spy = new SpyNotifier();
        var thresholdId = await SeedObjectiveAsync(new SloDefinition
        {
            Name = "No adyen 5xx",
            Kind = SloKind.MetricThreshold,
            Dimension = WarpMetricsOptions.AspNetCoreRequestDuration,
            TagFilter = $"{RouteTag},http.response.status_code=5*",
            TargetValue = 0,
            WindowSeconds = 3600,
            Enabled = true,
        });
        var ratioId = await SeedObjectiveAsync(new SloDefinition
        {
            Name = "Payments mostly succeed",
            Kind = SloKind.MetricRatio,
            Dimension = PaymentResults,
            RatioTag = "result",
            GoodValues = "success",
            BadValues = "error",
            TargetValue = 0.4,
            WindowSeconds = 3600,
            Enabled = true,
        });

        await using var server = await WarpTestServer.StartWithFakeTime(
            Fixture,
            time,
            configure: x => x.DisableWorker(),
            configureServices: x => x.AddSingleton<IWarpNotifier>(spy));

        // ---- Emit: N requests through the AddWarp-only app, two of them 500s. ----
        var commands = new RequestScopeCommandCounter();
        var meterName = $"Test.Payments.{Guid.NewGuid():N}";
        var emitter = await StartEmitterAsync(time, meterName, commands, connectionString, isPostgres);
        var client = emitter.GetTestClient();
        foreach (var outcome in Traffic)
        {
            using var response = await client.GetAsync($"/api/adyen/{outcome}", Ct);
            response.StatusCode.ShouldBe(string.Equals(outcome, "error", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }

        // ASP.NET Core records the request duration after the response is handed back, so wait until Warp's own
        // aggregator holds all six of this test's measurements before stopping — read from the collector's state
        // itself, not from a second listener whose callback order would have to be assumed.
        var aggregator = emitter.Services.GetRequiredService<MetricAggregator>();
        await WarpTestServer.WaitUntil(
            () => Task.FromResult(CountRouteMeasurements(aggregator) == Traffic.Length),
            TimeSpan.FromSeconds(5),
            Ct);

        // R2: the handler that emitted did no DB I/O, and nothing reached metric_point while the host ran. The
        // interceptor below only sees the emitter's TContext; it counts commands on the request's async flow only, so
        // the application heartbeat (its own flow) does not muddy it.
        commands.InRequest.ShouldBe(0);
        (await PointsAsync()).ShouldBeEmpty();

        // Graceful stop: the flusher's final drain writes the partial fine rows on TContext.
        await emitter.StopAsync(Ct);
        await emitter.DisposeAsync();
        commands.Total.ShouldBeGreaterThan(0, "the flush went through the emitter's TContext, so the counter is live");

        var flushed = await PointsAsync();
        flushed.ShouldNotBeEmpty();
        flushed.ShouldAllBe(x => x.Tier == MetricTier.Fine && !x.Compacted && x.BucketStart == Bucket);

        // ---- Server tick 1, once the bucket is closed (5-min bucket + 2 × 30s flush margin): compact, evaluate. ----
        time.Advance(TimeSpan.FromMinutes(7));
        await RunTickAsync<MetricRollup<TestContext>>(server);
        await RunTickAsync<SloEvaluator<TestContext>>(server);

        // Assert (a), points rolled up — SC1: one compacted fine row per series, with the exact count and sum per result tag.
        var fine = await PointsAsync(PaymentResults);
        fine.ShouldAllBe(x => x.Tier == MetricTier.Fine && x.Compacted && x.BucketStart == Bucket);
        Totals(fine).ShouldBe(["result=error:2:2", "result=refused:1:1", "result=success:3:3"]);

        // Assert (b), HTTP tags — SC5: the built-in instrument, keyed by the route TEMPLATE, with exactly the three allowlisted tags.
        var http = (await PointsAsync(WarpMetricsOptions.AspNetCoreRequestDuration))
            .Where(x => x.Tags.Contains(RouteTag, StringComparison.Ordinal))
            .ToList();
        http.ShouldNotBeEmpty();
        http.ShouldAllBe(x => x.Bounds != null && x.Buckets != null);
        http.Select(x => TagKeys(x.Tags)).ShouldAllBe(x => x.SequenceEqual(AspNetCoreTagKeys));
        http.Select(x => $"{x.Tags}:{x.Count}").Order(StringComparer.Ordinal).ShouldBe(
        [
            $"http.request.method=GET,http.response.status_code=200,{RouteTag}:4",
            $"http.request.method=GET,http.response.status_code=500,{RouteTag}:2",
        ]);
        (await PointsAsync(WarpMetricsOptions.AspNetCoreRequestDuration))
            .ShouldAllBe(x => !x.Tags.Contains("/api/adyen/success", StringComparison.Ordinal));

        // Assert (c), SLO edge: the 5xx threshold (N = 0) breached exactly once, labelled with its metric.
        var breach = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>();
        breach.Type.ShouldBe(WarpEventType.SloBreached);
        breach.Name.ShouldBe("No adyen 5xx");
        breach.Metric.ShouldBe($"{WarpMetricsOptions.AspNetCoreRequestDuration} [{RouteTag},http.response.status_code=5*]");
        var threshold = await EvaluationAsync(thresholdId);
        threshold.State.ShouldBe(SloState.Breaching);
        threshold.Attainment.ShouldBe(2);

        // The ratio counts success (3) against error (2) and ignores refused: 0.6 against a 0.4 target is healthy.
        var ratio = await EvaluationAsync(ratioId);
        ratio.State.ShouldBe(SloState.Healthy);
        ratio.Attainment.ShouldBe(0.6, 1e-9);

        // ---- Server tick 2, past FineResolutionRetention (6h): the compacted fine rows roll to hourly. ----
        // Assert (a, continued): the rolled-up totals survive the hourly roll unchanged.
        time.Advance(TimeSpan.FromHours(7));
        await RunTickAsync<MetricRollup<TestContext>>(server);

        var hourly = await PointsAsync(PaymentResults);
        hourly.ShouldAllBe(x => x.Tier == MetricTier.Hourly && x.BucketStart == Bucket);
        Totals(hourly).ShouldBe(Totals(fine));

        // Assert (d), job not claimed — R14: the service-only server ran all of the above and never claimed the seeded job.
        var state = await Fixture.CreateContext().Set<Job>()
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => x.CurrentState)
            .FirstAsync(Ct);
        state.ShouldBe(State.Enqueued);
    }

    // This test's http.server.request.duration measurements currently held by the emitter's aggregator. It has no
    // non-destructive read, so the probe drains and merges every snapshot straight back: MergeBack is additive and a
    // measurement recorded meanwhile lands in a fresh entry, so the pair is lossless (MetricAggregatorTests pins both
    // halves). Nothing else drains it here — the fake clock never fires the periodic flush.
    private static long CountRouteMeasurements(MetricAggregator aggregator)
    {
        var drained = aggregator.Drain();
        var seen = drained
            .Where(x => string.Equals(x.Series.Instrument.Name, WarpMetricsOptions.AspNetCoreRequestDuration, StringComparison.Ordinal))
            .Where(x => x.Series.Tags.Contains(RouteTag, StringComparison.Ordinal))
            .Sum(x => x.Count);
        aggregator.MergeBack(drained).ShouldBeEmpty();

        return seen;
    }

    // Through the task's own scope, then its post-commit hook: no ambient transaction here, so SaveChanges commits
    // and OnCommittedAsync dispatches the SLO edge — the two halves the task host runs around a tick.
    private static async Task RunTickAsync<TTask>(WarpTestServer server)
        where TTask : IServerTask
    {
        await using var scope = server.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var task = scope.ServiceProvider.GetServices<IServerTask>().OfType<TTask>().Single();

        await task.ExecuteAsync(Ct);
        await task.OnCommittedAsync(Ct);
    }

    private static List<string> Totals(IEnumerable<MetricPoint> rows)
        =>
        [
            .. rows
                .GroupBy(x => x.Tags, StringComparer.Ordinal)
                .Select(x => $"{x.Key}:{x.Sum(y => y.Count)}:{x.Sum(y => y.Sum)}")
                .Order(StringComparer.Ordinal),
        ];

    private static List<string> TagKeys(string tags) => [.. tags.Split(',').Select(x => x.Split('=')[0])];

    private static async Task<WebApplication> StartEmitterAsync(
        FakeTimeProvider time,
        string meterName,
        RequestScopeCommandCounter commands,
        string connectionString,
        bool isPostgres)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        // Before AddWarp, whose TryAddSingleton(TimeProvider.System) then leaves it in place.
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(x => new PaymentMetrics(x.GetRequiredService<IMeterFactory>(), meterName));
        builder.Services.AddDbContext<TestContext>(x => ConfigureContext(x, commands, connectionString, isPostgres));
        builder.Services.AddWarp<TestContext>(x =>
        {
            x.ApplicationName = Application;
            x.AddMetrics(y => y.CollectMeter(meterName).CollectAspNetCoreHttp());
        });

        var app = builder.Build();

        // Marks the request's async flow for the command counter, around the endpoint that emits.
        app.Use(async (context, next) =>
        {
            using (commands.EnterRequest())
            {
                await next(context);
            }
        });

        app.MapGet(RouteTemplate, (string id, PaymentMetrics metrics) =>
        {
            metrics.Record(id);

            return string.Equals(id, "error", StringComparison.Ordinal)
                ? Results.StatusCode(StatusCodes.Status500InternalServerError)
                : Results.Ok();
        });

        await app.StartAsync(Ct);

        return app;
    }

    private static void ConfigureContext(DbContextOptionsBuilder builder, RequestScopeCommandCounter commands, string connectionString, bool isPostgres)
    {
        if (isPostgres)
        {
            builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        }
        else
        {
            builder.UseSqlServer(connectionString);
        }

        builder.AddInterceptors(commands);
    }

    private async Task<(string ConnectionString, bool IsPostgres)> ProbeAsync()
    {
        await using var probe = Fixture.CreateContext();

        return (probe.Database.GetConnectionString()!, probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true);
    }

    private async Task<Guid> SeedEnqueuedJobAsync()
    {
        await using var ctx = Fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName!,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = Start.UtcDateTime,
            ScheduleTime = Start.UtcDateTime,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<int> SeedObjectiveAsync(SloDefinition definition)
    {
        definition.CreatedAt = Start.UtcDateTime;
        definition.UpdatedAt = Start.UtcDateTime;
        await using var ctx = Fixture.CreateContext();
        ctx.Set<SloDefinition>().Add(definition);
        await ctx.SaveChangesAsync(Ct);

        return definition.Id;
    }

    private async Task<List<MetricPoint>> PointsAsync(string? instrument = null)
    {
        await using var ctx = Fixture.CreateContext();
        var query = ctx.Set<MetricPoint>()
            .AsNoTracking()
            .Where(x => x.Application == Application);

        if (instrument is not null)
        {
            query = query.Where(x => x.Instrument == instrument);
        }

        return await query
            .OrderBy(x => x.Id)
            .ToListAsync(Ct);
    }

    private async Task<SloEvaluation> EvaluationAsync(int id)
    {
        await using var ctx = Fixture.CreateContext();
        var evaluation = await ctx.Set<SloEvaluation>()
            .AsNoTracking()
            .Where(x => x.SloDefinitionId == id)
            .FirstOrDefaultAsync(Ct);

        return evaluation.ShouldNotBeNull();
    }

    // The emitter's instrumentation: the standard IMeterFactory API, no Warp dependency (the host code under test).
    private sealed class PaymentMetrics
    {
        private readonly Counter<long> _results;

        public PaymentMetrics(IMeterFactory meterFactory, string meterName)
        {
            _results = meterFactory.Create(meterName).CreateCounter<long>(PaymentResults);
        }

        public void Record(string result) => _results.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    // Counts EF commands on the emitter's TContext: all of them, and those issued on a request's async flow.
    private sealed class RequestScopeCommandCounter : DbCommandInterceptor
    {
        private readonly AsyncLocal<bool> _inRequest = new();
        private int _total;
        private int _requestCommands;

        public int Total => Volatile.Read(ref _total);

        public int InRequest => Volatile.Read(ref _requestCommands);

        public IDisposable EnterRequest()
        {
            _inRequest.Value = true;

            return new Exit(_inRequest);
        }

        public override System.Data.Common.DbCommand CommandCreated(CommandEndEventData eventData, System.Data.Common.DbCommand result)
        {
            Interlocked.Increment(ref _total);
            if (_inRequest.Value)
            {
                Interlocked.Increment(ref _requestCommands);
            }

            return result;
        }

        private sealed class Exit(AsyncLocal<bool> flag) : IDisposable
        {
            public void Dispose() => flag.Value = false;
        }
    }
}
