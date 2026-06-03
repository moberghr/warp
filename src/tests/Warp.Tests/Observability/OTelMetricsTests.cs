using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Logging;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class OTelMetricsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    protected OTelMetricsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

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
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private WarpWorkerService<TestContext> CreateWorker(string queue, BarrierSignal? barrier = null)
    {
        var queues = new[] { queue };
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddSingleton<ActivityCapture>();
        services.AddSingleton(barrier ?? new BarrierSignal());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.TryAddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddRetry(o =>
        {
            o.MaxRetries = 3;
            o.Delays = [];
        });

        var workerConfig = new OptionsWrapper<WarpWorkerConfiguration>(new WarpWorkerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = queues,
            EnableHandlerLogging = true,
        });
        services.AddSingleton<IOptions<WarpWorkerConfiguration>>(workerConfig);
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
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals);
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

    [TimedFact]
    public async Task GetAndProcessJob_Completed_RecordsDurationMetric()
    {
        // Arrange — unique queue isolates from parallel tests
        var queue = $"metrics-duration-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(queue);
        double recordedDuration = -1;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.duration", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue))
            {
                recordedDuration = value;
            }
        });
        listener.Start();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        recordedDuration.ShouldBeGreaterThanOrEqualTo(0);
    }

    [TimedFact]
    public async Task GetAndProcessJob_Completed_RecordsCompletedMetricWithSucceededStatus()
    {
        // Arrange
        var queue = $"metrics-completed-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(queue);
        long completedCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.completed", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue) && HasTag(tags, "status", "succeeded"))
            {
                completedCount += value;
            }
        });
        listener.Start();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        completedCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_Failed_RecordsCompletedMetricWithFailedStatus()
    {
        // Arrange
        var queue = $"metrics-failed-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object> { ["MaxRetries"] = 0 }),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(queue);
        long failedCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.completed", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue) && HasTag(tags, "status", "failed"))
            {
                failedCount += value;
            }
        });
        listener.Start();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        failedCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task Enqueue_RecordsEnqueuedMetric()
    {
        // Arrange — publisher uses unique queue, no worker needed
        var queue = $"metrics-enqueue-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
        long enqueuedCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.enqueued", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue))
            {
                enqueuedCount += value;
            }
        });
        listener.Start();

        // Act
        await publisher.Enqueue(new UnitRequest(), queue);

        // Assert
        enqueuedCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_IncrementsAndDecrementsActiveMetric()
    {
        // Arrange
        var queue = $"metrics-active-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(queue);
        long activeNet = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.active", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue))
            {
                activeNet += value;
            }
        });
        listener.Start();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — +1 before handler, -1 in finally = net 0
        activeNet.ShouldBe(0);
    }

    [TimedFact]
    public async Task GetAndProcessJob_TwoWorkersConcurrently_ActiveMetricReachesTwo()
    {
        // Arrange — two barrier jobs on same queue
        var queue = $"metrics-concurrent-{Guid.NewGuid():N}";
        var barrier = new BarrierSignal();
        var ctx = _fixture.CreateContext();

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(BarrierRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new BarrierRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(BarrierRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new BarrierRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMilliseconds(1),
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker1 = CreateWorker(queue, barrier);
        var worker2 = CreateWorker(queue, barrier);
        long peakActive = 0;
        long currentActive = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.active", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue))
            {
                var newActive = Interlocked.Add(ref currentActive, value);
                long oldPeak;
                do
                {
                    oldPeak = Interlocked.Read(ref peakActive);
                }
                while (newActive > oldPeak && Interlocked.CompareExchange(ref peakActive, newActive, oldPeak) != oldPeak);
            }
        });
        listener.Start();

        // Act — start both workers concurrently
        var task1 = worker1.GetAndProcessJob(CancellationToken.None);
        var task2 = worker2.GetAndProcessJob(CancellationToken.None);

        // Wait for both handlers to be running
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Both are active now — release them
        barrier.CanFinish.Release(2);
        await Task.WhenAll(task1, task2);

        // Assert
        peakActive.ShouldBe(2);
        Interlocked.Read(ref currentActive).ShouldBe(0);
    }

    [TimedFact]
    public async Task GetAndProcessJob_FailedWithRetries_RecordsRetriedStatus()
    {
        // Arrange
        var queue = $"metrics-retry-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
            MaxRetries = 3,
            RetriedTimes = 0,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(queue);
        long retriedCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.completed", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue) && HasTag(tags, "status", "retried"))
            {
                retriedCount += value;
            }
        });
        listener.Start();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        retriedCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task StartBatch_RecordsEnqueuedMetricForBatchAndChildren()
    {
        // Arrange
        var queue = $"metrics-batch-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        var batchPublisher = new BatchPublisher<TestContext>(ctx, Options.Create(new WarpConfiguration { DefaultQueue = queue }), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
        long jobEnqueued = 0;
        long batchEnqueued = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.enqueued", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "queue", queue) && HasTag(tags, "kind", "job"))
            {
                jobEnqueued += value;
            }

            if (HasTag(tags, "queue", queue) && HasTag(tags, "kind", "batch"))
            {
                batchEnqueued += value;
            }
        });
        listener.Start();

        // Act
        await batchPublisher.StartNew(new List<UnitRequest> { new(), new(), new() });

        // Assert
        jobEnqueued.ShouldBe(3);
        batchEnqueued.ShouldBe(1);
    }
}
