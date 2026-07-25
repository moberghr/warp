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
using Warp.Core.Retry;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

/// <summary>
/// Part 2 (aggregate METRICS via OTel meters). The always-on <c>warp.job.execution.*</c> meters emit at
/// finalization regardless of <see cref="WarpConfiguration.JobMetricsSink"/> (null-listener ⇒ zero cost),
/// carrying the jobstat dimensions (job.type / outcome / executor application). <c>JobMetricsSink</c> gates
/// ONLY the write-optimised <c>jobstat</c> <see cref="Counter"/> rows on the finalization path — Otel skips
/// them, Database/Both keep them — while the app-agnostic <c>stats:*</c> counters and the meters are
/// unaffected. Meter capture mirrors <c>OTelMetricsTests</c>; the Counter-vs-not assertions read the DB.
/// </summary>
[GenerateDatabaseTests]
public abstract class OTelJobMetricsSinkTestsBase : IAsyncLifetime
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string TypeUnit = typeof(UnitRequest).AssemblyQualifiedName!;
    private static readonly string TypeThrow = typeof(ThrowExceptionRequest).AssemblyQualifiedName!;

    private readonly IDatabaseFixture _fixture;

    // Unique per test instance so the process-global MeterListener (shared with the parallel PostgreSql /
    // SqlServer variant and other tests) can filter to THIS test's measurements via the application tag.
    private readonly string _appName = $"worker-app-{Guid.NewGuid():N}";

    protected OTelJobMetricsSinkTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = WorkerId,
            ServerId = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Completed_EmitsExecutionMeter_WithTypeOutcomeAndApplicationTags()
    {
        var jobType = await SeedJobAsync(TypeUnit, new UnitRequest());
        var worker = CreateWorker(RecordingSink.Database);

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        var durations = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureExecutionMeters(_appName, totals, durations);

        await worker.GetAndProcessJob(Ct);

        var total = totals.ShouldHaveSingleItem();
        total[WarpTelemetryAttributes.JobMeterType].ShouldBe(jobType);
        total[WarpTelemetryAttributes.JobMeterOutcome].ShouldBe("succeeded");
        total[WarpTelemetryAttributes.MeterApplication].ShouldBe(_appName);

        // Duration histogram fires with the same dimensions.
        durations.ShouldHaveSingleItem()[WarpTelemetryAttributes.JobMeterOutcome].ShouldBe("succeeded");
    }

    [TimedFact]
    public async Task Failed_EmitsExecutionMeter_WithFailedOutcome()
    {
        await SeedJobAsync(
            TypeThrow,
            new ThrowExceptionRequest(),
            metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["MaxRetries"] = 0 }));
        var worker = CreateWorker(RecordingSink.Database);

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        var durations = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureExecutionMeters(_appName, totals, durations);

        await worker.GetAndProcessJob(Ct);

        var total = totals.ShouldHaveSingleItem();
        total[WarpTelemetryAttributes.JobMeterOutcome].ShouldBe("failed");
        total[WarpTelemetryAttributes.MeterApplication].ShouldBe(_appName);
    }

    [TimedFact]
    public async Task JobMetricsSink_Otel_SkipsJobStatCounterRows_ButMeterStillFires()
    {
        await SeedJobAsync(TypeUnit, new UnitRequest());
        var worker = CreateWorker(RecordingSink.Otel);

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureExecutionMeters(_appName, totals, []);

        await worker.GetAndProcessJob(Ct);

        // The meter still fired (aggregate data goes to the collector) ...
        totals.ShouldHaveSingleItem();

        // ... but NO jobstat Counter rows were written on the finalization path (the perf win).
        (await CountJobStatCountersAsync()).ShouldBe(0);

        // The app-agnostic lifecycle stats:* counters are untouched by the gate.
        (await CountStatsSucceededCountersAsync()).ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task JobMetricsSink_Database_WritesJobStatCounterRows()
    {
        await SeedJobAsync(TypeUnit, new UnitRequest());
        var worker = CreateWorker(RecordingSink.Database);

        await worker.GetAndProcessJob(Ct);

        (await CountJobStatCountersAsync()).ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task JobMetricsSink_Both_WritesJobStatCounterRows()
    {
        await SeedJobAsync(TypeUnit, new UnitRequest());
        var worker = CreateWorker(RecordingSink.Both);

        await worker.GetAndProcessJob(Ct);

        (await CountJobStatCountersAsync()).ShouldBeGreaterThan(0);
    }

    private async Task<string> SeedJobAsync(string type, object payload, string? metadata = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = type,
            Message = JsonSerializer.Serialize(payload),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Ct);

        return type;
    }

    private async Task<int> CountJobStatCountersAsync()
        => await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith(JobStatsKeys.Prefix + ":"))
            .CountAsync(Ct);

    private async Task<int> CountStatsSucceededCountersAsync()
        => await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith("stats:succeeded"))
            .CountAsync(Ct);

    private static MeterListener CaptureExecutionMeters(
        string appName,
        List<IReadOnlyDictionary<string, object?>> totals,
        List<IReadOnlyDictionary<string, object?>> durations)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && (string.Equals(instrument.Name, "warp.job.execution.total", StringComparison.Ordinal)
                        || string.Equals(instrument.Name, "warp.job.execution.duration", StringComparison.Ordinal)))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, WarpTelemetryAttributes.MeterApplication, appName))
            {
                totals.Add(Snapshot(tags));
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, WarpTelemetryAttributes.MeterApplication, appName))
            {
                durations.Add(Snapshot(tags));
            }
        });

        listener.Start();

        return listener;
    }

    private static Dictionary<string, object?> Snapshot(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            snapshot[tag.Key] = tag.Value;
        }

        return snapshot;
    }

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, string value)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal)
                && string.Equals(tag.Value?.ToString(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        services.AddSingleton<MultiHandlerCounter>();
        services.AddSingleton<ActivityCapture>();
        services.AddSingleton(new BarrierSignal());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.TryAddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddRetry(o =>
        {
            o.MaxRetries = 3;
            o.Delays = [];
        });

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

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = queues,
        };

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
