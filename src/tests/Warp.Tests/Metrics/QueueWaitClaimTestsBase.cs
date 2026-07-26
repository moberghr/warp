using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Logging;
using Warp.Core.Observability;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Metrics;

/// <summary>
/// Queue-wait SLI at the claim site (§8.26). Driven through the real
/// <see cref="WarpWorkerService{TContext}.GetAndProcessJob"/> so the queue-wait write rides the same
/// "Processing" JobLog SaveChanges (no extra round-trip, §0.2/§6.1). Pins: the <c>qwait:</c> Counter written
/// at claim with <c>dur ≈ claim − ScheduleTime</c>; the always-on <c>warp.job.queue.wait</c> meter; and the
/// <see cref="WarpConfiguration.JobMetricsSink"/> gate (Otel skips the Counters, meter still fires).
/// Harness mirrors <c>OTelJobMetricsSinkTests</c>.
/// </summary>
[GenerateDatabaseTests]
public abstract class QueueWaitClaimTestsBase : IAsyncLifetime
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string TypeUnit = typeof(UnitRequest).AssemblyQualifiedName!;

    private readonly IDatabaseFixture _fixture;
    private readonly string _appName = $"queue-app-{Guid.NewGuid():N}";

    protected QueueWaitClaimTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server { Id = ServerId, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow, ServiceCount = 1 });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker { Id = WorkerId, ServerId = ServerId, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Claim_WritesQueueWaitCounter_WithWaitFromScheduleTime()
    {
        await SeedJobAsync(scheduleTime: DateTime.UtcNow.AddSeconds(-5));
        var worker = CreateWorker(RecordingSink.Database);

        await worker.GetAndProcessJob(Ct);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<Counter>().Where(x => x.Key == "qwait:default:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);

        // ~5s wait; allow slack for scheduling. Bucket 5000 covers 2500<ms<=5000.
        (await ctx.Set<Counter>().Where(x => x.Key == "qwait:default:dur").SumAsync(x => x.Value, Ct)).ShouldBeGreaterThanOrEqualTo(3000);
        (await ctx.Set<Counter>().Where(x => x.Key.StartsWith("qwait:default:pct:")).CountAsync(Ct)).ShouldBeGreaterThan(0);

        // The claim site is configured with ApplicationName = _appName, so the executor-app slice is written too.
        (await ctx.Set<Counter>().Where(x => x.Key == $"qwait-app:{_appName}:default:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Claim_MeasuresWaitFromScheduleTime_NotCreateTime()
    {
        // Simulate a requeue: created an hour ago, but ScheduleTime advanced to ~2s ago (RequeueJob resets it).
        // Wait must be measured from ScheduleTime (~2s), never from the original CreateTime (~1h).
        await SeedJobAsync(scheduleTime: DateTime.UtcNow.AddSeconds(-2), createTime: DateTime.UtcNow.AddHours(-1));
        var worker = CreateWorker(RecordingSink.Database);

        await worker.GetAndProcessJob(Ct);

        var dur = await _fixture.CreateContext().Set<Counter>().Where(x => x.Key == "qwait:default:dur").SumAsync(x => x.Value, Ct);
        dur.ShouldBeGreaterThanOrEqualTo(1000);           // ~2s from ScheduleTime
        dur.ShouldBeLessThan(60_000);                     // nowhere near the 1h CreateTime (~3.6M ms)
    }

    [TimedFact]
    public async Task Claim_JobMetricsSinkOtel_SkipsCounters_ButMeterFires()
    {
        await SeedJobAsync(scheduleTime: DateTime.UtcNow.AddSeconds(-2));
        var worker = CreateWorker(RecordingSink.Otel);

        var waits = new List<(double Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = CaptureQueueWaitMeter(_appName, waits);

        await worker.GetAndProcessJob(Ct);

        // Meter fired with the right queue tag AND a sensible recorded value (~2s), not 0 ...
        var measurement = waits.ShouldHaveSingleItem();
        measurement.Tags[WarpTelemetryAttributes.QueueMeterQueue].ShouldBe("default");
        measurement.Value.ShouldBeGreaterThanOrEqualTo(1000);

        // ... but no qwait Counter rows on the hot path (the perf win).
        (await _fixture.CreateContext().Set<Counter>().Where(x => x.Key.StartsWith("qwait:")).CountAsync(Ct)).ShouldBe(0);
    }

    private async Task SeedJobAsync(DateTime scheduleTime, DateTime? createTime = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = TypeUnit,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = createTime ?? scheduleTime,
            ScheduleTime = scheduleTime,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private static MeterListener CaptureQueueWaitMeter(string appName, List<(double Value, IReadOnlyDictionary<string, object?> Tags)> waits)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.job.queue.wait", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            var mine = false;
            foreach (var tag in tags)
            {
                snapshot[tag.Key] = tag.Value;
                if (string.Equals(tag.Key, WarpTelemetryAttributes.MeterApplication, StringComparison.Ordinal)
                    && string.Equals(tag.Value?.ToString(), appName, StringComparison.Ordinal))
                {
                    mine = true;
                }
            }

            if (mine)
            {
                waits.Add((value, snapshot));
            }
        });

        listener.Start();

        return listener;
    }

    private WarpWorkerService<TestContext> CreateWorker(RecordingSink sink)
    {
        var queues = new[] { "default" };
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton<CounterService>();
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.TryAddSingleton(TimeProvider.System);

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = queues,
            EnableHandlerLogging = true,
            ApplicationName = _appName,
            JobMetricsSink = sink,
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration { WorkerCount = 1, Queues = queues };

        return new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            TestTasks.NullTransport,
            TestTasks.NullSignals);
    }
}
