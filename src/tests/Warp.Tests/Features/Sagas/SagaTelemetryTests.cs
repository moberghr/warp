using System.Diagnostics.Metrics;
using Shouldly;
using Warp.Core.Handlers;
using Warp.Core.Sagas;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Features.Sagas;

[Trait("Category", "NoDb")]
public class SagaTelemetryTests
{
    [TimedFact]
    public async Task NewSaga_FromStartsSaga_IncrementsSagasStarted()
    {
        var count = 0L;
        using var listener = StartListener("warp.sagas.started", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)))
            {
                count += value;
            }
        });

        var (store, locks, jobContext, cache, time) = SetUp();
        var proxy = new SagaHandlerProxy<TelemetrySaga, StartTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartTelemetry { CorrelationKey = "started" }, CancellationToken.None);

        count.ShouldBe(1);
    }

    [TimedFact]
    public async Task ExistingSagaCompletes_IncrementsSagasCompleted()
    {
        var count = 0L;
        using var listener = StartListener("warp.sagas.completed", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)))
            {
                count += value;
            }
        });

        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("done", new TelemetrySaga { CorrelationKey = "done" });
        var proxy = new SagaHandlerProxy<TelemetrySaga, CompleteTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new CompleteTelemetry { CorrelationKey = "done" }, CancellationToken.None);

        count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveConflict_AfterMarkCompleted_DoesNotIncrementSagasCompleted()
    {
        // Regression: counter previously fired inside HandleExistingSaga *before* TrySaveAsync.
        // On version conflict the proxy requeues; the retry runs the handler again and would
        // increment the counter a second time. The proxy now gates lifecycle counters on a
        // clean save (Outcome == null) so SaveCounted reflects logical completions exactly.
        var completedCount = 0L;
        using var completedListener = StartListener("warp.sagas.completed", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)))
            {
                completedCount += value;
            }
        });

        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("conflict", new TelemetrySaga { CorrelationKey = "conflict" });
        store.ThrowConflictKindOnNextSave = SagaSaveConflictKind.Version;

        var proxy = new SagaHandlerProxy<TelemetrySaga, CompleteTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new CompleteTelemetry { CorrelationKey = "conflict" }, CancellationToken.None);

        // Requeue outcome was set; SagasCompleted must NOT have fired because the save rolled back.
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.LogMessage!.ShouldContain("version conflict");
        completedCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task UniqueConstraintConflict_IncrementsRequeued_WithUniqueReason()
    {
        // Closes the previously-untested reason=unique tag path. Mirrors the busy/version
        // tag-asserting tests; without this any future copy-paste error swapping the tag
        // values in the production emit would be silent.
        var count = 0L;
        using var listener = StartListener("warp.sagas.requeued", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)) && HasTag(tags, "reason", "unique"))
            {
                count += value;
            }
        });

        var (store, locks, jobContext, cache, time) = SetUp();
        store.ThrowConflictKindOnNextSave = SagaSaveConflictKind.UniqueConstraint;
        var proxy = new SagaHandlerProxy<TelemetrySaga, StartTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartTelemetry { CorrelationKey = "race" }, CancellationToken.None);

        count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SagasLive_IncrementsOnStart_DecrementsOnComplete()
    {
        // Per-process gauge that an OTel exporter aggregates across worker replicas. Started
        // is +1, Completed is -1; the net effect for a single-message lifecycle that runs
        // start + complete on the same worker is zero.
        var live = 0L;
        using var listener = StartListener("warp.sagas.live", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)))
            {
                live += value;
            }
        });

        // 1) Start: a new saga via [StartsSaga] should bump the gauge by 1.
        var (store, locks, jobContext, cache, time) = SetUp();
        var startProxy = new SagaHandlerProxy<TelemetrySaga, StartTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);
        await startProxy.HandleAsync(new StartTelemetry { CorrelationKey = "lifecycle" }, CancellationToken.None);
        live.ShouldBe(1);

        // 2) Complete: a follow-up message that calls MarkCompleted brings it back to 0.
        var completeCtx = new JobContext();
        var completeProxy = new SagaHandlerProxy<TelemetrySaga, CompleteTelemetry>(
            new TelemetryHandler(), store, locks, completeCtx, time, cache);
        await completeProxy.HandleAsync(new CompleteTelemetry { CorrelationKey = "lifecycle" }, CancellationToken.None);
        live.ShouldBe(0);
    }

    [TimedFact]
    public async Task MutexBusy_IncrementsSagasRequeued_WithBusyReason()
    {
        var count = 0L;
        using var listener = StartListener("warp.sagas.requeued", (value, tags) =>
        {
            if (HasTag(tags, "saga_type", nameof(TelemetrySaga)) && HasTag(tags, "reason", "busy"))
            {
                count += value;
            }
        });

        var (store, locks, jobContext, cache, time) = SetUp();
        await using var holder = locks.HoldLock($"warp:saga:{typeof(TelemetrySaga).FullName}:contended");
        var proxy = new SagaHandlerProxy<TelemetrySaga, CompleteTelemetry>(
            new TelemetryHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new CompleteTelemetry { CorrelationKey = "contended" }, CancellationToken.None);

        count.ShouldBe(1);
    }

    private static MeterListener StartListener(string instrumentName, Action<long, ReadOnlySpan<KeyValuePair<string, object?>>> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => onMeasurement(value, tags));
        listener.Start();

        return listener;
    }

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, string value)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal) && Equals(tag.Value, value))
            {
                return true;
            }
        }

        return false;
    }

    private static (FakeSagaStore store, FakeLockProvider locks, JobContext jobContext, SagaCorrelationCache cache, TimeProvider time) SetUp()
    {
        return (new FakeSagaStore(), new FakeLockProvider(), new JobContext(), new SagaCorrelationCache(), TimeProvider.System);
    }

    public sealed class TelemetrySaga : Saga;

    [StartsSaga]
    public sealed class StartTelemetry : IMessage
    {
        [Correlate]
        public string CorrelationKey { get; set; } = string.Empty;
    }

    public sealed class CompleteTelemetry : IMessage
    {
        [Correlate]
        public string CorrelationKey { get; set; } = string.Empty;
    }

    private sealed class TelemetryHandler :
        ISagaHandler<TelemetrySaga, StartTelemetry>,
        ISagaHandler<TelemetrySaga, CompleteTelemetry>
    {
        public Task HandleAsync(TelemetrySaga saga, StartTelemetry message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandleAsync(TelemetrySaga saga, CompleteTelemetry message, CancellationToken cancellationToken)
        {
            saga.MarkCompleted();
            return Task.CompletedTask;
        }
    }
}
