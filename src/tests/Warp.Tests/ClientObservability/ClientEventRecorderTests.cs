using Shouldly;
using Warp.Core.ClientObservability;
using Warp.Core.Enums;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// NoDb coverage for the client-event recorder (bounded lossy channel) and the cardinality guard (§8.27).
/// Pins that a full buffer drops (returns false, never blocks the browser) and that browser-controlled names
/// collapse to <c>{other}</c> beyond the per-type cap while bounded names (vitals/levels) pass through.
/// </summary>
[Trait("Category", "NoDb")]
public class ClientEventRecorderTests
{
    private static ClientEventRecord Event(ClientEventType type = ClientEventType.Log)
        => new() { Application = "app", Type = type };

    [Fact]
    public void Record_ChannelFull_ReturnsFalse_AndDoesNotThrow()
    {
        var recorder = new DbClientEventRecorder(capacity: 2);

        recorder.Record(Event()).ShouldBeTrue();
        recorder.Record(Event()).ShouldBeTrue();

        // Third write into a full buffer drops — lossy by design, the caller counts it and never blocks.
        recorder.Record(Event()).ShouldBeFalse();
    }

    [Fact]
    public void Cardinality_CollapsesBeyondCap_ButKeepsSeenNames()
    {
        var guard = new ClientEventCardinality(maxErrorNames: 2, maxEventNames: 100);

        guard.Resolve(ClientEventType.Error, "TypeError").ShouldBe("TypeError");
        guard.Resolve(ClientEventType.Error, "RangeError").ShouldBe("RangeError");
        guard.Resolve(ClientEventType.Error, "TypeError").ShouldBe("TypeError");     // already seen ⇒ kept
        guard.Resolve(ClientEventType.Error, "SyntaxError").ShouldBe("{other}");     // over cap ⇒ collapsed
    }

    [Fact]
    public void Cardinality_NeverCollapsesVitalsOrLevels_AndPassesNullThrough()
    {
        var guard = new ClientEventCardinality(maxErrorNames: 1, maxEventNames: 1);

        // Vitals + log levels are inherently bounded — never collapsed regardless of cap.
        guard.Resolve(ClientEventType.Vital, "LCP").ShouldBe("LCP");
        guard.Resolve(ClientEventType.Vital, "CLS").ShouldBe("CLS");
        guard.Resolve(ClientEventType.Log, "warn").ShouldBe("warn");
        guard.Resolve(ClientEventType.Log, "error").ShouldBe("error");

        // Null name ⇒ null (no per-name key).
        guard.Resolve(ClientEventType.Log, null).ShouldBeNull();
    }
}
