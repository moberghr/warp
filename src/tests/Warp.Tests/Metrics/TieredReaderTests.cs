using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.ClientObservability;
using Warp.Core.Endpoints;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for the tier-awareness added to the per-family history readers (§8.30) — the code path that
/// actually surfaces a bucket <c>StatisticRollup</c> rolled to a marked hourly/daily tier. Each parser must
/// accept the legacy unmarked hourly shape AND the marked <c>:h1:</c>/<c>:d1:</c> shapes, down-binning a daily
/// bucket to its hour so rolled data still charts. Without this, rolled-to-daily history would be silently
/// dropped (the gap this fix closes).
/// </summary>
[Trait("Category", "NoDb")]
public class TieredReaderTests
{
    private static readonly DateTime Hour14 = new(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Midnight = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AdapterHistory_AcceptsLegacyHourlyAndMarkedTiers_DownbinningDaily()
    {
        AdapterCounterKeys.TryParseHistory("adapter:vendor:hist:failed:2026-07-20-14", out var a, out var o, out var h).ShouldBeTrue();
        a.ShouldBe("vendor");
        o.ShouldBe("failed");
        h.ShouldBe(Hour14);

        AdapterCounterKeys.TryParseHistory("adapter:vendor:hist:failed:h1:2026-07-20-14", out _, out _, out var hh).ShouldBeTrue();
        hh.ShouldBe(Hour14);

        AdapterCounterKeys.TryParseHistory("adapter:vendor:hist:failed:d1:2026-07-20", out var ad, out var od, out var hd).ShouldBeTrue();
        ad.ShouldBe("vendor");
        od.ShouldBe("failed");
        hd.ShouldBe(Midnight); // daily down-binned to its hour

        AdapterCounterKeys.TryParseHistory("adapter:vendor:failed", out _, out _, out _).ShouldBeFalse();       // lifetime
        AdapterCounterKeys.TryParseHistory("adapter:vendor:pct:100", out _, out _, out _).ShouldBeFalse();      // lifetime pct
    }

    [Fact]
    public void EndpointHistory_AcceptsLegacyHourlyAndMarkedTiers_DownbinningDaily()
    {
        EndpointCounterKeys.TryParseHistory("endpoint:GET-api-x:hist:success:2026-07-20-14", out var r, out var o, out var h).ShouldBeTrue();
        r.ShouldBe("GET-api-x");
        o.ShouldBe("success");
        h.ShouldBe(Hour14);

        EndpointCounterKeys.TryParseHistory("endpoint:GET-api-x:hist:success:d1:2026-07-20", out var rd, out var od, out var hd).ShouldBeTrue();
        rd.ShouldBe("GET-api-x");
        od.ShouldBe("success");
        hd.ShouldBe(Midnight);

        EndpointCounterKeys.TryParseHistory("endpoint:GET-api-x:success", out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void ClientTypeHistory_AcceptsLegacyHourlyAndMarkedTiers_DownbinningDaily()
    {
        ClientEventKeys.TryParseTypeHistory("clientevent:total:error:hist:2026-07-20-14", out var t, out var h).ShouldBeTrue();
        t.ShouldBe("error");
        h.ShouldBe("2026-07-20-14");

        ClientEventKeys.TryParseTypeHistory("clientevent:total:error:hist:d1:2026-07-20", out var td, out var hd).ShouldBeTrue();
        td.ShouldBe("error");
        hd.ShouldBe("2026-07-20-00"); // daily down-binned to the midnight hour string the reader parses

        ClientEventKeys.TryParseTypeHistory("clientevent:total:error:count", out _, out _).ShouldBeFalse();
    }
}
