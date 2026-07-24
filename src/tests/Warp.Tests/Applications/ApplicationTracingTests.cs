using System.Diagnostics;
using Shouldly;
using Warp.Core.Logging;
using Warp.Tests.Helpers;

namespace Warp.Tests.Applications;

/// <summary>
/// Cross-application trace origin (Batch 8): every Warp-created Activity carries a <c>warp.application</c>
/// tag equal to the process's <c>WarpConfiguration.ApplicationName</c> when set, and carries no such tag
/// when unset (opt-in, additive). The value lives on the process-wide <see cref="WarpTelemetry.ApplicationName"/>
/// static (a deploy-time constant per process), which <c>AddWarp</c> sets once. Because it is a shared static,
/// each test sets it explicitly and resets it in a <c>finally</c> rather than relying on registration order,
/// so a parallel run can never observe a leaked value. xUnit runs a class's tests sequentially, so the
/// set/reset pairs here never race each other, and no other test class touches this static.
///
/// Span capture uses the AsyncLocal-sentinel <see cref="ActivityListenerHarness"/> constructed in the test-method
/// body (never in async init) so process-global listener capture stays isolated across parallel test classes.
/// </summary>
[Trait("Category", "NoDb")]
public class ApplicationTracingTests
{
    [TimedFact]
    public void JobActivity_ApplicationNameSet_CarriesWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = "orders-api";
        try
        {
            using var harness = new ActivityListenerHarness();

            using var activity = WarpTelemetry.StartJobActivity(Guid.NewGuid(), null, "default");

            activity.ShouldNotBeNull();
            activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBe("orders-api");
        }
        finally
        {
            WarpTelemetry.ApplicationName = null;
        }
    }

    [TimedFact]
    public void ProducerActivity_ApplicationNameSet_CarriesWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = "orders-api";
        try
        {
            using var harness = new ActivityListenerHarness();

            using var activity = WarpTelemetry.StartProducerActivity("default", WarpTelemetryAttributes.OperationSend);

            activity.ShouldNotBeNull();
            activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBe("orders-api");
        }
        finally
        {
            WarpTelemetry.ApplicationName = null;
        }
    }

    [TimedFact]
    public void AdapterActivity_ApplicationNameSet_CarriesWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = "orders-api";
        try
        {
            using var harness = new ActivityListenerHarness();

            using var activity = WarpTelemetry.StartAdapterActivity("payments", "Charge");

            activity.ShouldNotBeNull();
            activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBe("orders-api");
        }
        finally
        {
            WarpTelemetry.ApplicationName = null;
        }
    }

    [TimedFact]
    public void ReceiveActivity_ApplicationNameSet_CarriesWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = "orders-api";
        try
        {
            using var harness = new ActivityListenerHarness();

            using var activity = WarpTelemetry.StartReceiveActivity("default");

            activity.ShouldNotBeNull();
            activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBe("orders-api");
        }
        finally
        {
            WarpTelemetry.ApplicationName = null;
        }
    }

    [TimedFact]
    public void JobActivity_ApplicationNameUnset_NoWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = null;

        using var harness = new ActivityListenerHarness();

        using var activity = WarpTelemetry.StartJobActivity(Guid.NewGuid(), null, "default");

        activity.ShouldNotBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBeNull();
    }

    [TimedFact]
    public void ProducerActivity_ApplicationNameUnset_NoWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = null;

        using var harness = new ActivityListenerHarness();

        using var activity = WarpTelemetry.StartProducerActivity("default", WarpTelemetryAttributes.OperationSend);

        activity.ShouldNotBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBeNull();
    }

    [TimedFact]
    public void AdapterActivity_ApplicationNameUnset_NoWarpApplicationTag()
    {
        WarpTelemetry.ApplicationName = null;

        using var harness = new ActivityListenerHarness();

        using var activity = WarpTelemetry.StartAdapterActivity("payments", "Charge");

        activity.ShouldNotBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpApplication).ShouldBeNull();
    }
}
