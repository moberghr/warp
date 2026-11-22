using Npgsql;
using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Notifications;

// The NOTIFY payload codec is two static functions. The round trip through a real connection stays in PostgresNotificationTransportTests.
// Moved out of PostgresNotificationTransportTests, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class PostgresNotificationPayloadTests
{
    [Fact]
    public void Encode_Decode_RoundTripsJobEnqueuedWithQueue()
    {
        var payload = PostgresNotificationTransport.Encode(NotificationKind.JobEnqueued, "critical");
        PostgresNotificationTransport.TryDecode(payload, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(NotificationKind.JobEnqueued);
        parsed.Queue.ShouldBe("critical");
    }

    [Fact]
    public void Encode_Decode_RoundTripsMessageEnqueued()
    {
        var payload = PostgresNotificationTransport.Encode(NotificationKind.MessageEnqueued, null);
        PostgresNotificationTransport.TryDecode(payload, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(NotificationKind.MessageEnqueued);
        parsed.Queue.ShouldBeNull();
    }

    [Fact]
    public void Encode_Decode_RoundTripsJobFinalized()
    {
        var payload = PostgresNotificationTransport.Encode(NotificationKind.JobFinalized, null);
        PostgresNotificationTransport.TryDecode(payload, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(NotificationKind.JobFinalized);
        parsed.Queue.ShouldBeNull();
    }

    [Fact]
    public void Decode_Garbage_ReturnsFalse()
    {
        PostgresNotificationTransport.TryDecode("Xxx", out _).ShouldBeFalse();
        PostgresNotificationTransport.TryDecode(null, out _).ShouldBeFalse();
        PostgresNotificationTransport.TryDecode(string.Empty, out _).ShouldBeFalse();
        PostgresNotificationTransport.TryDecode("J", out _).ShouldBeFalse();
    }
}
