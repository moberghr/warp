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
    public void ExtractTopFrame_SkipsReflectionInvokeStub_ReturnsHandlerFrame()
    {
        var stack =
            "System.TimeoutException: boom\n" +
            "   at Acme.Orders.ChargeOrderHandler.HandleAsync(Cmd c) in DemoFaults.cs:line 20\n" +
            "   at InvokeStub_ChargeOrderHandler.HandleAsync(Object, Span`1)\n" +
            "   at System.Reflection.RuntimeMethodInfo.Invoke(Object obj)";

        ErrorFingerprint.ExtractTopFrame(stack, Denylist).ShouldBe("Acme.Orders.ChargeOrderHandler.HandleAsync");
    }

    [Fact]
    public void ExtractTopFrame_WhenHandlerIsFramework_NeverLandsOnUnstableInvokeStub()
    {
        // A handler in a denylisted namespace is skipped — but the JIT-named reflection stub right below it must
        // NOT become the locus (its name varies → splits one issue into many); fall through instead.
        var stack =
            "System.TimeoutException: boom\n" +
            "   at Warp.Handlers.SomeHandler.HandleAsync(Cmd c)\n" +
            "   at InvokeStub_SomeHandler.HandleAsync(Object, Span`1)\n" +
            "   at System.Reflection.RuntimeMethodInfo.Invoke(Object obj)";

        ErrorFingerprint.ExtractTopFrame(stack, Denylist).ShouldBeNull();
    }

    [Fact]
    public void ExtractTopFrame_NormalizesAsyncStateMachineFrame()
    {
        var stack = "System.Exception: x\n   at Acme.Orders.ChargeOrderHandler+<HandleAsync>d__3.MoveNext()";

        ErrorFingerprint.ExtractTopFrame(stack, Denylist).ShouldBe("Acme.Orders.ChargeOrderHandler.HandleAsync");
    }

    [Fact]
    public void FromException_UnwrapsReflectionAndAggregateWrappersToTheRealCause()
    {
        // Reflection-invoked handlers surface a TargetInvocationException wrapping the real exception — group on
        // the real cause so a TimeoutException doesn't masquerade as one big TargetInvocationException issue.
        var real = new TimeoutException("Payment gateway did not respond for order 42");
        var wrapped = new System.Reflection.TargetInvocationException("plumbing", real);

        var occ = ErrorOccurrenceFactory.FromException(ErrorSource.Job, wrapped, "ChargeOrderRequest", null, null, new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));

        occ.ExceptionType.ShouldBe("System.TimeoutException");
        occ.Message.ShouldBe("Payment gateway did not respond for order 42");
        occ.Stack.ShouldNotBeNull().ShouldContain("TargetInvocationException");   // full wrapper kept as the sample

        // AggregateException unwraps too.
        ErrorOccurrenceFactory
            .FromException(ErrorSource.Job, new AggregateException(new InvalidOperationException("boom")), "X", null, null, new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc))
            .ExceptionType.ShouldBe("System.InvalidOperationException");
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
