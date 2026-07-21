using Shouldly;
using Warp.Core.Adapters;
using Warp.Tests.Helpers;

namespace Warp.Tests.Adapters;

/// <summary>
/// NoDb group-dimension coverage (SC15/SC16): a group is recorded on the call record and the span
/// attribute for every outcome, becomes a meter tag only when <c>IncludeGroupInMetrics</c> is set,
/// group-less calls behave exactly as before, and distinct group values beyond
/// <c>MaxDistinctGroups</c> collapse to <c>{other}</c>.
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterGroupTests
{
    [TimedFact]
    public void CallWithGroup_RecordsGroupNameOnRecord()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: "webhook");

        adapters.BeginCall("webhook", "order.created", "shop-1").Succeed();

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBe("shop-1");
    }

    [TimedFact]
    public void SetGroupAfterBeginCall_RecordsGroupNameOnRecord()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: "webhook");

        var scope = adapters.BeginCall("webhook", "order.created");
        scope.SetGroup("shop-2");
        scope.Succeed();

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBe("shop-2");
    }

    [TimedFact]
    public void GroupLessCall_HasNullGroupOnRecord()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: "webhook");

        adapters.BeginCall("webhook", "order.created").Succeed();

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBeNull();
    }

    [TimedFact]
    public void CallWithGroup_SetsSpanGroupAttribute()
    {
        using var harness = new ActivityListenerHarness();
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: "grp-span");

        adapters.BeginCall("grp-span", "order.created", "shop-1").Succeed();

        harness.FirstByName("grp-span.order.created")!.GetTagItem("warp.adapter.group").ShouldBe("shop-1");
    }

    [TimedFact]
    public void GroupLessCall_HasNoSpanGroupAttribute()
    {
        using var harness = new ActivityListenerHarness();
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: "grp-none");

        adapters.BeginCall("grp-none", "order.created").Succeed();

        harness.FirstByName("grp-none.order.created")!.GetTagItem("warp.adapter.group").ShouldBeNull();
    }

    [TimedFact]
    public void CallWithGroup_DefaultOptions_GroupIsNotAMeterTag()
    {
        var adapterName = "grp-nometric";
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = AdapterTestHarness.CaptureLong("warp.adapter.calls", adapterName, measurements);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        adapters.BeginCall(adapterName, "order.created", "shop-1").Succeed();

        measurements.ShouldHaveSingleItem().ContainsKey("group").ShouldBeFalse();
    }

    [TimedFact]
    public void CallWithGroup_IncludeGroupInMetrics_GroupIsAMeterTag()
    {
        var adapterName = "grp-metric";
        var options = new WarpAdapterOptions { IncludeGroupInMetrics = true };
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = AdapterTestHarness.CaptureLong("warp.adapter.calls", adapterName, measurements);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(options, adapterName);

        adapters.BeginCall(adapterName, "order.created", "shop-1").Succeed();

        measurements.ShouldHaveSingleItem()["group"].ShouldBe("shop-1");
    }

    [TimedFact]
    public void GroupsBeyondMaxDistinct_CollapseToOther()
    {
        var options = new WarpAdapterOptions { MaxDistinctGroups = 2 };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options, "grp-cap");

        adapters.BeginCall("grp-cap", "order.created", "shop-1").Succeed();
        adapters.BeginCall("grp-cap", "order.created", "shop-2").Succeed();
        adapters.BeginCall("grp-cap", "order.created", "shop-3").Succeed();

        recorder.Records.Count.ShouldBe(3);
        recorder.Records[0].GroupName.ShouldBe("shop-1");
        recorder.Records[1].GroupName.ShouldBe("shop-2");
        recorder.Records[2].GroupName.ShouldBe("{other}");
    }

    [TimedFact]
    public void RepeatedGroup_DoesNotConsumeCapTwice()
    {
        // Group analogue of OperationNameResolverTests.Cardinality_RepeatedHeuristic_DoesNotConsumeCapTwice:
        // re-recording a group already inside the cap must not burn a second slot, so a later distinct
        // group still fits under the cap rather than collapsing to {other}.
        var options = new WarpAdapterOptions { MaxDistinctGroups = 2 };
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(options, "grp-repeat");

        adapters.BeginCall("grp-repeat", "order.created", "shop-1").Succeed();
        adapters.BeginCall("grp-repeat", "order.created", "shop-1").Succeed();
        adapters.BeginCall("grp-repeat", "order.created", "shop-2").Succeed();

        recorder.Records.Count.ShouldBe(3);
        recorder.Records[0].GroupName.ShouldBe("shop-1");
        recorder.Records[1].GroupName.ShouldBe("shop-1");
        recorder.Records[2].GroupName.ShouldBe("shop-2");
    }
}
