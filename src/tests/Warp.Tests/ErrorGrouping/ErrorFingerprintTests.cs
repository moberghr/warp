using Shouldly;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// The pure grouping function (§8.29): message normalization (PII-safe Title + message-varying occurrences
/// collapse), stable identity, and top in-app frame extraction across .NET and browser stack shapes.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ErrorFingerprintTests
{
    private static readonly IReadOnlyList<string> Denylist = ErrorFingerprint.DefaultInAppDenylist;

    [Fact]
    public void NormalizeMessage_CollapsesVariableParts()
    {
        ErrorFingerprint.NormalizeMessage("Order 12345 not found").ShouldBe("Order <num> not found");
        ErrorFingerprint.NormalizeMessage("User 'jane@x.com' is blocked").ShouldBe("User <str> is blocked");
        ErrorFingerprint.NormalizeMessage("key 3f2504e0-4f89-41d3-9a0c-0305e82c3301 expired").ShouldBe("key <guid> expired");
        ErrorFingerprint.NormalizeMessage("chunk a1b2c3d4e5 failed").ShouldBe("chunk <hex> failed");
    }

    [Fact]
    public void NormalizeMessage_TwoOrderIds_ProduceIdenticalTitle()
    {
        ErrorFingerprint.NormalizeMessage("Order 12345 not found")
            .ShouldBe(ErrorFingerprint.NormalizeMessage("Order 67890 not found"));
    }

    [Fact]
    public void Compute_IsStableAndDiscriminates()
    {
        var a = ErrorFingerprint.Compute(ErrorSource.Job, "System.NullReferenceException", "Acme.ProcessOrderHandler.Handle");
        var same = ErrorFingerprint.Compute(ErrorSource.Job, "System.NullReferenceException", "Acme.ProcessOrderHandler.Handle");
        var otherLocus = ErrorFingerprint.Compute(ErrorSource.Job, "System.NullReferenceException", "Acme.SaveOrderHandler.Handle");
        var otherType = ErrorFingerprint.Compute(ErrorSource.Job, "System.TimeoutException", "Acme.ProcessOrderHandler.Handle");
        var otherSource = ErrorFingerprint.Compute(ErrorSource.Client, "System.NullReferenceException", "Acme.ProcessOrderHandler.Handle");

        a.Length.ShouldBe(32);
        a.ShouldBe(same);
        a.ShouldNotBe(otherLocus);      // different culprit → different issue
        a.ShouldNotBe(otherType);       // different exception type → different issue
        a.ShouldNotBe(otherSource);     // same type from another surface → different issue
    }

    [Fact]
    public void ExtractTopFrame_DotNet_SkipsFrameworkFramesReturnsAppFrame()
    {
        var stack =
            "System.NullReferenceException: Object reference not set to an instance of an object.\n" +
            "   at System.String.Format(String format, Object arg0)\n" +
            "   at Acme.Orders.ProcessOrderHandler.Handle(ProcessOrder cmd) in ProcessOrderHandler.cs:line 42\n" +
            "   at Warp.Worker.WarpWorkerService.Execute(Job job)";

        ErrorFingerprint.ExtractTopFrame(stack, Denylist).ShouldBe("Acme.Orders.ProcessOrderHandler.Handle");
    }

    [Fact]
    public void ExtractTopFrame_DoesNotMistakeMessageLineForAFrame()
    {
        // App-namespaced exception type on the message line must NOT be returned as the frame.
        var stack =
            "Acme.Domain.OrderException: something broke\n" +
            "   at Acme.Orders.OrderTotals.Compute(Cart cart) in OrderTotals.cs:line 9";

        ErrorFingerprint.ExtractTopFrame(stack, Denylist).ShouldBe("Acme.Orders.OrderTotals.Compute");
    }

    [Fact]
    public void ExtractTopFrame_BrowserStack_ReducesToFileBasename()
    {
        var chrome = "TypeError: Cannot read properties of undefined\n    at onClick (https://shop.test/assets/Checkout.tsx:42:18)\n    at HTMLButtonElement.dispatch (https://shop.test/assets/react-dom.js:12:1)";
        ErrorFingerprint.ExtractTopFrame(chrome, Denylist).ShouldBe("Checkout.tsx");

        var firefox = "handleClick@https://shop.test/assets/Checkout.tsx:42:18\ndispatch@https://shop.test/assets/react-dom.js:9:2";
        ErrorFingerprint.ExtractTopFrame(firefox, Denylist).ShouldBe("Checkout.tsx");
    }

    [Fact]
    public void ExtractTopFrame_NoParseableFrame_ReturnsNull()
    {
        ErrorFingerprint.ExtractTopFrame("something went wrong", Denylist).ShouldBeNull();
        ErrorFingerprint.ExtractTopFrame(null, Denylist).ShouldBeNull();
    }

    [Fact]
    public void ComputeForStatusCode_GroupsByStatusAndRoute()
    {
        var a = ErrorFingerprint.ComputeForStatusCode(422, "POST /api/checkout");
        var sameStatusOtherRoute = ErrorFingerprint.ComputeForStatusCode(422, "GET /api/orders");
        var otherStatusSameRoute = ErrorFingerprint.ComputeForStatusCode(400, "POST /api/checkout");

        a.ShouldBe(ErrorFingerprint.ComputeForStatusCode(422, "POST /api/checkout"));
        a.ShouldNotBe(sameStatusOtherRoute);
        a.ShouldNotBe(otherStatusSameRoute);
    }
}
