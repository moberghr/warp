using Microsoft.Extensions.Time.Testing;
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
    public void Cardinality_CollapsesErrorEventAndLogNamesBeyondCap()
    {
        var guard = new ClientEventCardinality(maxErrorNames: 2, maxEventNames: 100, maxLogNames: 1);

        guard.Resolve(ClientEventType.Error, "TypeError").ShouldBe("TypeError");
        guard.Resolve(ClientEventType.Error, "RangeError").ShouldBe("RangeError");
        guard.Resolve(ClientEventType.Error, "TypeError").ShouldBe("TypeError");     // already seen ⇒ kept
        guard.Resolve(ClientEventType.Error, "SyntaxError").ShouldBe("{other}");     // over cap ⇒ collapsed

        // Logs collapse too (their dimension is the level, which a hostile client can forge).
        guard.Resolve(ClientEventType.Log, "warn").ShouldBe("warn");
        guard.Resolve(ClientEventType.Log, "error").ShouldBe("{other}");             // over cap of 1
    }

    [Fact]
    public void Cardinality_VitalsFollowAllowlistNotACap()
    {
        var guard = new ClientEventCardinality(maxErrorNames: 1, maxEventNames: 1, maxLogNames: 1);

        // Known Core Web Vitals keep their (canonical, upper-cased) name regardless of cap...
        guard.Resolve(ClientEventType.Vital, "LCP").ShouldBe("LCP");
        guard.Resolve(ClientEventType.Vital, "cls").ShouldBe("CLS");   // case-normalized

        // ...an unknown/hostile vital name collapses to {other}, so it can't explode the keyspace.
        guard.Resolve(ClientEventType.Vital, "totally-made-up").ShouldBe("{other}");

        // Null name ⇒ null (no per-name key).
        guard.Resolve(ClientEventType.Log, null).ShouldBeNull();
    }

    [Fact]
    public void RateLimiter_TripsWithinWindow_ThenResetsAfterAMinute()
    {
        var time = new FakeTimeProvider();
        var limiter = new ClientIngestRateLimiter(perMinute: 2, time);

        limiter.TryAcquire("ip", 1).ShouldBeTrue();
        limiter.TryAcquire("ip", 1).ShouldBeTrue();
        limiter.TryAcquire("ip", 1).ShouldBeFalse();     // cap reached inside the window

        time.Advance(TimeSpan.FromMinutes(1));
        limiter.TryAcquire("ip", 1).ShouldBeTrue();       // window rolled over ⇒ admitted again
    }

    [Fact]
    public void RateLimiter_BoundsTrackingTable_FailsClosedWhenFull_ThenPrunesExpired()
    {
        var time = new FakeTimeProvider();
        var limiter = new ClientIngestRateLimiter(perMinute: 10, time, maxTrackedKeys: 2);

        limiter.TryAcquire("a", 1).ShouldBeTrue();
        limiter.TryAcquire("b", 1).ShouldBeTrue();

        // Table full and nothing expired ⇒ a new IP fails closed (bounds memory) rather than adding an entry.
        limiter.TryAcquire("c", 1).ShouldBeFalse();

        // Once the earlier windows age out, the new IP is admitted (their entries are pruned to make room).
        time.Advance(TimeSpan.FromMinutes(1));
        limiter.TryAcquire("c", 1).ShouldBeTrue();
    }
}
