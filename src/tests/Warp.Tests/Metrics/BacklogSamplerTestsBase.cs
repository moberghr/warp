using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Observability;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// Backlog SLI sampler (§8.26): per-queue depth (eligible Enqueued count) + oldest-job age, sampled off the
/// hot path. Pins the grouped count/min, that Scheduled + future-ScheduleTime rows are excluded, that a
/// drained queue resets to 0, the always-on gauge, and the <see cref="WarpConfiguration.JobMetricsSink"/>
/// gate (Otel writes no Statistic rows). Direct-construction, both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class BacklogSamplerTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected BacklogSamplerTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Sample_ComputesPerQueueDepthAndOldestAge_ExcludingScheduledAndFuture()
    {
        var now = DateTime.UtcNow;
        await SeedAsync("default", State.Enqueued, now.AddSeconds(-30));
        await SeedAsync("default", State.Enqueued, now.AddSeconds(-10));
        await SeedAsync("default", State.Enqueued, now.AddSeconds(-5));
        await SeedAsync("email", State.Enqueued, now.AddSeconds(-8));
        await SeedAsync("default", State.Scheduled, now.AddSeconds(-60));   // not Enqueued → excluded
        await SeedAsync("default", State.Enqueued, now.AddHours(1));        // future ScheduleTime → excluded

        await CreateSampler(RecordingSink.Database).ExecuteAsync(Ct);

        var stats = await LoadBacklogStatsAsync();
        stats["qbacklog:default:depth"].ShouldBe(3);
        stats["qbacklog:default:oldest_age_seconds"].ShouldBeGreaterThanOrEqualTo(25);   // ~30s, slack
        stats["qbacklog:email:depth"].ShouldBe(1);
    }

    [TimedFact]
    public async Task Sample_DrainedQueue_ResetsDepthToZero()
    {
        var id = await SeedAsync("default", State.Enqueued, DateTime.UtcNow.AddSeconds(-5));

        await CreateSampler(RecordingSink.Database).ExecuteAsync(Ct);
        (await LoadBacklogStatsAsync())["qbacklog:default:depth"].ShouldBe(1);

        // Drain the queue, then resample — the previously-recorded depth must fall to 0, not stay stale.
        var ctx = _fixture.CreateContext();
        await ctx.Set<Job>().Where(x => x.Id == id).ExecuteDeleteAsync(Ct);

        await CreateSampler(RecordingSink.Database).ExecuteAsync(Ct);
        (await LoadBacklogStatsAsync())["qbacklog:default:depth"].ShouldBe(0);
    }

    [TimedFact]
    public async Task Sample_JobMetricsSinkOtel_WritesNoStatistic_ButGaugeReportsDepth()
    {
        await SeedAsync("default", State.Enqueued, DateTime.UtcNow.AddSeconds(-5));

        var depths = new List<(string Queue, long Depth)>();
        var ages = new List<(string Queue, double Age)>();
        using var depthListener = CaptureDepthGauge(depths);
        using var ageListener = CaptureOldestAgeGauge(ages);

        await CreateSampler(RecordingSink.Otel).ExecuteAsync(Ct);

        // No Statistic rows under Otel ...
        (await _fixture.CreateContext().Set<Statistic>().Where(x => x.Key.StartsWith("qbacklog")).CountAsync(Ct)).ShouldBe(0);

        // ... but BOTH ObservableGauges report the sampled backlog.
        depthListener.RecordObservableInstruments();
        ageListener.RecordObservableInstruments();
        depths.ShouldContain(x => x.Queue == "default" && x.Depth == 1);
        ages.ShouldContain(x => x.Queue == "default" && x.Age >= 4);   // ~5s, slack
    }

    private async Task<Guid> SeedAsync(string queue, State state, DateTime scheduleTime)
    {
        var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = state,
            Type = "Test.Type",
            Message = JsonSerializer.Serialize(new { }),
            CreateTime = scheduleTime,
            ScheduleTime = scheduleTime,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<Dictionary<string, long>> LoadBacklogStatsAsync()
        => await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key.StartsWith("qbacklog"))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.Ordinal, Ct);

    private BacklogSampler<TestContext> CreateSampler(RecordingSink sink)
        => new(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(new WarpServerConfiguration { JobMetricsSink = sink }));

    private static MeterListener CaptureDepthGauge(List<(string Queue, long Depth)> depths)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.job.queue.depth", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            string? queue = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, WarpTelemetryAttributesQueue, StringComparison.Ordinal))
                {
                    queue = tag.Value?.ToString();
                }
            }

            if (queue is not null)
            {
                depths.Add((queue, value));
            }
        });

        listener.Start();

        return listener;
    }

    private static MeterListener CaptureOldestAgeGauge(List<(string Queue, double Age)> ages)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.job.queue.oldest_age_seconds", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            string? queue = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, WarpTelemetryAttributesQueue, StringComparison.Ordinal))
                {
                    queue = tag.Value?.ToString();
                }
            }

            if (queue is not null)
            {
                ages.Add((queue, value));
            }
        });

        listener.Start();

        return listener;
    }

    private const string WarpTelemetryAttributesQueue = "queue";
}
