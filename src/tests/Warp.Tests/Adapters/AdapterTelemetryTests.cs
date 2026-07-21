using System.Diagnostics;
using Shouldly;
using Warp.Tests.Helpers;

namespace Warp.Tests.Adapters;

/// <summary>
/// NoDb telemetry coverage (SC10): every completed adapter call scope emits a <c>Client</c>-kind span
/// named <c>{adapter}.{operation}</c> and the <c>warp.adapter.calls</c> / <c>warp.adapter.duration</c>
/// meters, regardless of the recorder. Span assertions use the AsyncLocal-sentinel
/// <see cref="ActivityListenerHarness"/> constructed in the test-method body (never in async init) so
/// process-global listener capture stays isolated across parallel test classes
/// (tasks/lessons.md 2026-05-07).
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterTelemetryTests
{
    [TimedFact]
    public void CompletedCall_EmitsClientSpan_NamedAdapterDotOperation()
    {
        using var harness = new ActivityListenerHarness();
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: "tel-span");

        adapters.BeginCall("tel-span", "GetOrders").Succeed();

        var span = harness.FirstByName("tel-span.GetOrders");
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Client);
    }

    [TimedFact]
    public void CompletedCall_SpanCarriesAdapterOperationAndOutcomeTags()
    {
        using var harness = new ActivityListenerHarness();
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: "tel-tags");

        adapters.BeginCall("tel-tags", "GetOrders").Succeed();

        var span = harness.FirstByName("tel-tags.GetOrders");
        span.ShouldNotBeNull();
        span.GetTagItem("warp.adapter.name").ShouldBe("tel-tags");
        span.GetTagItem("warp.adapter.operation").ShouldBe("GetOrders");
        span.GetTagItem("warp.adapter.outcome").ShouldBe("Success");
    }

    [TimedFact]
    public void FailedCall_SpanHasErrorStatusAndErrorType()
    {
        using var harness = new ActivityListenerHarness();
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: "tel-fail");

        adapters.BeginCall("tel-fail", "GetOrders").Fail(new InvalidOperationException("boom"));

        var span = harness.FirstByName("tel-fail.GetOrders");
        span.ShouldNotBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("warp.adapter.outcome").ShouldBe("Failed");
        span.GetTagItem("error.type").ShouldBe(typeof(InvalidOperationException).FullName);
    }

    [TimedFact]
    public void CompletedCall_IncrementsCallsCounter_WithOutcomeTag()
    {
        var adapterName = "tel-calls";
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = AdapterTestHarness.CaptureLong("warp.adapter.calls", adapterName, measurements);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        adapters.BeginCall(adapterName, "GetOrders").Succeed();

        var tags = measurements.ShouldHaveSingleItem();
        tags["operation"].ShouldBe("GetOrders");
        tags["outcome"].ShouldBe("Success");
    }

    [TimedFact]
    public void CompletedCall_RecordsDurationHistogram()
    {
        var adapterName = "tel-duration";
        var durations = new List<double>();
        using var listener = AdapterTestHarness.CaptureDouble("warp.adapter.duration", adapterName, durations);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        adapters.BeginCall(adapterName, "GetOrders").Succeed();

        durations.ShouldHaveSingleItem();
    }

    [TimedFact]
    public void CompletedCall_NoActivityListener_StillIncrementsCallsCounter()
    {
        var adapterName = "tel-nolistener";
        var count = 0L;
        using var listener = AdapterTestHarness.StartCounterListener("warp.adapter.calls", adapterName, value => count += value);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        adapters.BeginCall(adapterName, "GetOrders").Succeed();

        count.ShouldBe(1);
    }
}
