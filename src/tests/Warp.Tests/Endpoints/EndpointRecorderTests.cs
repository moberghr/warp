using Shouldly;
using Warp.Core.Endpoints;
using Warp.Core.Enums;

namespace Warp.Tests.Endpoints;

/// <summary>
/// NoDb coverage for <see cref="DbEndpointCallRecorder"/>'s bounded buffer (fed from
/// <c>WarpConfiguration.CallLogBufferCapacity</c>). A full channel makes <see cref="DbEndpointCallRecorder.Record"/>
/// return false (the drop-when-full path) and never blocks; only the accepted records stay buffered.
/// </summary>
[Trait("Category", "NoDb")]
public class EndpointRecorderTests
{
    [TimedFact]
    public void Record_RealBoundedChannelFull_ReturnsFalse_AndKeepsAcceptedRecord()
    {
        var recorder = new DbEndpointCallRecorder(capacity: 1);

        recorder.Record(MakeRecord()).ShouldBeTrue();
        recorder.Record(MakeRecord()).ShouldBeFalse();

        var buffered = 0;
        while (recorder.Reader.TryRead(out _))
        {
            buffered++;
        }

        buffered.ShouldBe(1);
    }

    private static EndpointCallRecord MakeRecord()
        => new()
        {
            Method = "GET",
            RouteTemplate = "/ping",
            Operation = "Ping",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };
}
