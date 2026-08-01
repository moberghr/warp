using Shouldly;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// The inbox-row factory (§8.29): version/environment stamping flows through every source shape, and
/// <see cref="ErrorOccurrenceFactory.FromException"/> keeps the reflection unwrap. Pure, no DB.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ErrorOccurrenceFactoryTests
{
    [Fact]
    public void FromException_StampsVersionAndEnvironment()
    {
        var occurrence = ErrorOccurrenceFactory.FromException(
            ErrorSource.Job,
            new InvalidOperationException("boom"),
            "Acme.Orders.ProcessOrderRequest",
            traceId: null,
            application: "worker",
            timestamp: DateTime.UtcNow,
            version: "1.4.2",
            environment: "prod");

        occurrence.Version.ShouldBe("1.4.2");
        occurrence.Environment.ShouldBe("prod");
        occurrence.ExceptionType.ShouldBe("System.InvalidOperationException");
    }

    [Fact]
    public void FromException_Unwraps_ButKeepsVersion()
    {
        var inner = new InvalidOperationException("npe");
        var wrapper = new System.Reflection.TargetInvocationException(inner);

        var occurrence = ErrorOccurrenceFactory.FromException(
            ErrorSource.Job, wrapper, "culprit", traceId: null, application: null, timestamp: DateTime.UtcNow, version: "2.0.0", environment: "staging");

        occurrence.ExceptionType.ShouldBe("System.InvalidOperationException");   // unwrapped to the real cause
        occurrence.Version.ShouldBe("2.0.0");
        occurrence.Environment.ShouldBe("staging");
    }

    [Fact]
    public void FromError_StampsVersionAndEnvironment()
    {
        var occurrence = ErrorOccurrenceFactory.FromError(
            ErrorSource.Adapter, "System.TimeoutException", "timed out", stack: null, "vendor.GetOrders", traceId: null, application: "api", timestamp: DateTime.UtcNow, version: "3.1.0", environment: "prod");

        occurrence.Version.ShouldBe("3.1.0");
        occurrence.Environment.ShouldBe("prod");
    }

    [Fact]
    public void VersionAndEnvironment_DefaultToNull_WhenOmitted()
    {
        var occurrence = ErrorOccurrenceFactory.FromError(
            ErrorSource.Client, "TypeError", "boom", stack: null, "/checkout", traceId: null, application: "shop", timestamp: DateTime.UtcNow);

        occurrence.Version.ShouldBeNull();
        occurrence.Environment.ShouldBeNull();
    }
}
