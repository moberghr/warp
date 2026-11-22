using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Notifications;

// The Service Broker payload codec is two static functions. The round trip through a real connection stays in SqlServerNotificationTransportTests.
// Moved out of SqlServerNotificationTransportTests, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class SqlServerNotificationPayloadTests
{
    [Fact]
    public void Encode_Decode_RoundTripsJobEnqueuedWithQueue()
    {
        var payload = SqlServerNotificationTransport.Encode(NotificationKind.JobEnqueued, "critical");
        SqlServerNotificationTransport.TryDecode(payload, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(NotificationKind.JobEnqueued);
        parsed.Queue.ShouldBe("critical");
    }

    [Fact]
    public void Decode_Garbage_ReturnsFalse()
    {
        SqlServerNotificationTransport.TryDecode("Xxx", out _).ShouldBeFalse();
        SqlServerNotificationTransport.TryDecode(null, out _).ShouldBeFalse();
        SqlServerNotificationTransport.TryDecode(string.Empty, out _).ShouldBeFalse();
    }
}
