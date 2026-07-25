using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Enums;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Adapters;

/// <summary>
/// NoDb scope-lifecycle coverage: outcome recording, tags/correlation, FailuresOnly gating, dispose
/// defaulting, double-complete idempotency, and the drop-on-full-channel counter (SC1 scope-level).
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterScopeTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void Succeed_RecordsSuccessOutcome()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Success);
        record.AdapterName.ShouldBe("vendor");
        record.Operation.ShouldBe("GetOrders");
    }

    [TimedFact]
    public void Fail_RecordsFailedOutcome_WithExceptionDetails()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        record.ExceptionMessage.ShouldBe("boom");
    }

    [TimedFact]
    public void Fail_WithRateLimitedException_RecordsThrottledOutcome()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        adapters.BeginCall("vendor", "GetOrders").Fail(new AdapterRateLimitedException("limited"));

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Throttled);
    }

    [TimedFact]
    public void SetTag_IncludesTagOnRecord()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetTag("region", "eu");
        scope.Succeed();

        recorder.Records.ShouldHaveSingleItem().Tags.ShouldNotBeNull().ShouldContain(new KeyValuePair<string, string>("region", "eu"));
    }

    [TimedFact]
    public void SetCorrelation_IncludesCorrelationIdOnRecord()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetCorrelation("delivery-42");
        scope.Succeed();

        recorder.Records.ShouldHaveSingleItem().CorrelationId.ShouldBe("delivery-42");
    }

    [TimedFact]
    public void FailuresOnly_SuccessHandsRecord_FlaggedSuppressLog()
    {
        // FailuresOnly gates the call-log ROW only: the record is still handed over (so counters and
        // telemetry stay unaffected) but flagged SuppressLog so the flusher skips the log row.
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeTrue();
    }

    [TimedFact]
    public void FailuresOnly_FailureRecord_NotFlaggedSuppressLog()
    {
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
    }

    [TimedFact]
    public void FailuresOnly_FailureWritesRecord()
    {
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Failed);
    }

    [TimedFact]
    public void SampleRateZero_Success_FlaggedSuppressLog()
    {
        // SampleRate 0 keeps no successful ROWS (same effect as FailuresOnly for successes): the record is
        // still handed over (counters/telemetry unaffected) but flagged SuppressLog.
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeTrue();
    }

    [TimedFact]
    public void SampleRateZero_Failure_NotSuppressed()
    {
        // Failures are always kept regardless of the sample rate — the row is the point of a failure.
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
    }

    [TimedFact]
    public void SampleRateOne_Success_NotSuppressed()
    {
        // The keep-all default writes every successful row — no behaviour change for existing callers.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
    }

    [TimedFact]
    public void ForceCapture_SampleRateZero_Success_NotSuppressed()
    {
        // A forced successful call writes its row even when the sample rate would have dropped it.
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetForceCapture(true);
        scope.Succeed();

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
    }

    [TimedFact]
    public void ForceCapture_FailuresOnly_Success_NotSuppressed()
    {
        // Force-capture also overrides RecordCalls = FailuresOnly for the row-write decision.
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options);

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetForceCapture(true);
        scope.Succeed();

        recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
    }

    [TimedFact]
    public void Dispose_WithoutExplicitOutcome_RecordsSuccess()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        using (adapters.BeginCall("vendor", "GetOrders"))
        {
        }

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public void DoubleComplete_RecordsExactlyOnce()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.Succeed();
        scope.Fail(new InvalidOperationException("late"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task ConcurrentComplete_TwoTasksRaceSucceedAndFail_RecordsExactlyOnce()
    {
        // Two tasks pinned on a BarrierSignal (N=2, §4.7), then released together to complete the SAME scope
        // concurrently. The Interlocked.Exchange guard in Complete must let exactly one win — one record.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var scope = adapters.BeginCall("vendor", "GetOrders");
        var barrier = new BarrierSignal();

        var succeed = Task.Run(async () =>
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(Ct);
            scope.Succeed();
        });

        var fail = Task.Run(async () =>
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(Ct);
            scope.Fail(new InvalidOperationException("late"));
        });

        // Release both only once each has pinned on the barrier.
        await barrier.Running.WaitAsync(Ct);
        await barrier.Running.WaitAsync(Ct);
        barrier.CanFinish.Release(2);

        await Task.WhenAll(succeed, fail);

        recorder.Records.Count.ShouldBe(1);
    }

    [TimedFact]
    public void ChannelFull_RecordRejected_IncrementsRecordsDropped()
    {
        var adapterName = "drop-vendor";
        var dropped = 0L;
        using var listener = AdapterTestHarness.StartCounterListener("warp.adapter.records_dropped", adapterName, value => dropped += value);
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);
        recorder.Accept = false;

        adapters.BeginCall(adapterName, "GetThing").Succeed();

        dropped.ShouldBe(1);
    }

    [TimedFact]
    public void SetTag_NullValue_Throws()
    {
        // Consistency with the rest of the setter family (SetRequestSummary etc. all fail fast on null):
        // a null tag value must not flow silently into TagsJson serialisation.
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters();
        using var scope = adapters.BeginCall("vendor", "GetOrders");

        Should.Throw<ArgumentNullException>(() => scope.SetTag("key", null!));

        scope.Succeed();
    }

    [TimedFact]
    public void Complete_EnrichCallThrows_DoesNotPropagate_AndRecordStillLands()
    {
        // The EnrichCall guard is one of three INDEPENDENT try/catch blocks in Complete (telemetry and
        // recorder are its siblings, both already covered). A throwing enrichment hook must neither
        // propagate to the caller nor starve the durable record ("does not throw" alone is not an
        // assertion — the primary effect must still happen).
        var options = new WarpAdapterOptions
        {
            EnrichCall = _ => throw new InvalidOperationException("user enrichment bug"),
        };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options, adapterName: "enrich-throws");

        Should.NotThrow(() => adapters.BeginCall("enrich-throws", "GetOrders").Succeed());

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public void UsingScope_BodyThrows_DisposeRecordsSuccess_DocumentedFootgun()
    {
        // Pins the DOCUMENTED sharp edge on AdapterCallScope: Dispose cannot detect an exceptional unwind,
        // so a using-block whose body throws without an explicit Fail records Success. Manual-scope callers
        // must call Fail in their catch — the class doc says so. If this behaviour is ever changed
        // deliberately, e.g. to an Abandoned outcome, this pin is the test to update.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        Should.Throw<InvalidOperationException>(() =>
        {
            using var scope = adapters.BeginCall("vendor", "GetOrders");

            throw new InvalidOperationException("body failure without Fail(ex)");
        });

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public void Record_RealBoundedChannelFull_DropsAndCountsRecordsDropped()
    {
        // The real DbAdapterCallRecorder (not a fake with Accept=false): a full bounded channel makes
        // TryWrite return false, the drop is counted, and exactly the accepted record sits in the channel.
        var adapterName = "real-channel-vendor";
        var dropped = 0L;
        using var listener = AdapterTestHarness.StartCounterListener("warp.adapter.records_dropped", adapterName, value => dropped += value);

        var recorder = new DbAdapterCallRecorder(capacity: 1);
        var adapters = new WarpAdapters(new AdapterRegistry(), recorder, TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);

        adapters.BeginCall(adapterName, "GetOrders").Succeed();
        adapters.BeginCall(adapterName, "GetOrders").Succeed();

        dropped.ShouldBe(1);

        var buffered = 0;
        while (recorder.Reader.TryRead(out _))
        {
            buffered++;
        }

        buffered.ShouldBe(1);
    }

    [TimedFact]
    public void Complete_RecorderThrows_DoesNotPropagate_AndPreservesCallerException()
    {
        // The handler calls Fail(ex) inside its catch block; an unguarded recorder throw here would replace
        // the caller's real transport exception. Complete must swallow the recorder exception and let the
        // original propagate.
        var adapters = new WarpAdapters(new AdapterRegistry(), new ThrowingRecorder(), TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);
        var original = new InvalidOperationException("real transport failure");

        var caught = Should.Throw<InvalidOperationException>(() =>
        {
            var scope = adapters.BeginCall("vendor", "GetOrders");
            try
            {
                throw original;
            }
            catch (Exception ex)
            {
                scope.Fail(ex);

                throw;
            }
        });

        caught.ShouldBeSameAs(original);
    }

    [TimedFact]
    public void Complete_TelemetryListenerThrows_DoesNotPropagate()
    {
        var adapterName = "throw-telemetry-vendor";
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "adapter", StringComparison.Ordinal) && Equals(tag.Value, adapterName))
                {
                    throw new InvalidOperationException("listener boom");
                }
            }
        });

        listener.Start();

        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        Should.NotThrow(() => adapters.BeginCall(adapterName, "GetThing").Succeed());

        // Telemetry is best-effort: a throwing meter listener must not swallow the call record. The call
        // still lands with its true outcome — recording is not collateral damage of a telemetry failure.
        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Success);
        record.AdapterName.ShouldBe(adapterName);
    }

    [TimedFact]
    public void BeginCall_NameContainsColon_Throws()
    {
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters();

        Should.Throw<ArgumentException>(() => adapters.BeginCall("bad:name", "GetThing"));
    }

    [TimedFact]
    public void BeginCall_NameExceeds200Chars_Throws()
    {
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters();

        Should.Throw<ArgumentException>(() => adapters.BeginCall(new string('x', 201), "GetThing"));
    }
}

/// <summary>
/// Shared NoDb harness for the adapter scope tests: a capturing recorder and thin meter-listener
/// helpers scoped to a single adapter tag so the process-global <see cref="MeterListener"/> does not
/// pick up measurements from concurrently running test classes.
/// </summary>
internal static class AdapterTestHarness
{
    public static (WarpAdapters Adapters, CapturingRecorder Recorder, AdapterRegistry Registry) CreateAdapters(
        WarpAdapterOptions? options = null,
        string adapterName = "vendor")
    {
        var registry = new AdapterRegistry();
        if (options is not null)
        {
            registry.Register(adapterName, options);
        }

        var recorder = new CapturingRecorder();
        var adapters = new WarpAdapters(registry, recorder, TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);

        return (adapters, recorder, registry);
    }

    public static MeterListener StartCounterListener(string instrumentName, string adapterName, Action<long> onValue)
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

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "adapter", adapterName))
            {
                onValue(value);
            }
        });

        listener.Start();

        return listener;
    }

    public static MeterListener CaptureLong(string instrumentName, string adapterName, List<IReadOnlyDictionary<string, object?>> sink)
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

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "adapter", adapterName))
            {
                sink.Add(Snapshot(tags));
            }
        });

        listener.Start();

        return listener;
    }

    public static MeterListener CaptureDouble(string instrumentName, string adapterName, List<double> sink)
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

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, "adapter", adapterName))
            {
                sink.Add(value);
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
            if (string.Equals(tag.Key, key, StringComparison.Ordinal) && Equals(tag.Value, value))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class CapturingRecorder : IAdapterCallRecorder
{
    public List<AdapterCallRecord> Records { get; } = [];

    public bool Accept { get; set; } = true;

    public bool Record(AdapterCallRecord record)
    {
        if (!Accept)
        {
            return false;
        }

        Records.Add(record);

        return true;
    }
}

/// <summary>Recorder that throws from <see cref="Record"/> — exercises the scope's recording guard (F2).</summary>
internal sealed class ThrowingRecorder : IAdapterCallRecorder
{
    public bool Record(AdapterCallRecord record) => throw new InvalidOperationException("recorder boom");
}
