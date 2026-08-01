using Shouldly;
using Warp.Core.ClientObservability;
using Warp.Core.Enums;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// NoDb coverage for <see cref="ClientEventKeys"/> — the client-event Counter key family (§8.27). Pins the key
/// layout (per-type total + hourly history, per-name total, per-vital count/dur/pct histogram, per-app total),
/// the round-trip parsers + their mutual/foreign-family rejection, bucket assignment, CLS ×1000 scaling, and
/// the vital duration overflow clamp — so a regression in the key shape (which silently detaches the fold from
/// the reader) trips here.
/// </summary>
[Trait("Category", "NoDb")]
public class ClientEventKeysTests
{
    [Fact]
    public void Build_Error_EmitsTypeTotalHistoryAndNameTotal()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Error, name: "TypeError", value: null, application: null, hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "clientevent:total:error:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "clientevent:total:error:hist:2026-07-26-08" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "clientevent:name:error:TypeError:count" && c.Value == 1);

        // No vital keys and no per-app keys.
        counters.ShouldNotContain(c => c.Key.StartsWith("clientevent:vital:", StringComparison.Ordinal));
        counters.ShouldNotContain(c => c.Key.StartsWith("clientevent-app:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_Log_UsesNameDimensionForLevel()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Log, name: "warn", value: null, application: null, hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "clientevent:total:log:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "clientevent:name:log:warn:count" && c.Value == 1);
    }

    [Fact]
    public void Build_Vital_EmitsCountDurAndBucket()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Vital, name: "LCP", value: 2400, application: null, hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "clientevent:total:vital:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "clientevent:vital:LCP:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "clientevent:vital:LCP:dur" && c.Value == 2400);

        // 2400ms → smallest bucket bound >= 2400 is 2500.
        counters.ShouldContain(c => c.Key == "clientevent:vital:LCP:pct:2500" && c.Value == 1);

        // Vitals do NOT emit the generic per-name key.
        counters.ShouldNotContain(c => c.Key.StartsWith("clientevent:name:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ClsVital_ScalesByThousand()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Vital, name: "CLS", value: 0.15, application: null, hourBucket: "2026-07-26-08");

        // 0.15 → ×1000 = 150; dur sum stores the scaled integer, bucket bound >= 150 is 200.
        counters.ShouldContain(c => c.Key == "clientevent:vital:CLS:dur" && c.Value == 150);
        counters.ShouldContain(c => c.Key == "clientevent:vital:CLS:pct:200" && c.Value == 1);
    }

    [Fact]
    public void Build_HugeVital_ClampsDurationToIntMax_NoOverflow()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Vital, name: "LCP", value: 1e15, application: null, hourBucket: "2026-07-26-08");

        var dur = counters.Single(c => string.Equals(c.Key, "clientevent:vital:LCP:dur", StringComparison.Ordinal));
        dur.Value.ShouldBe(int.MaxValue);
        dur.Value.ShouldBeGreaterThan(0);   // never wraps negative
    }

    [Fact]
    public void Build_WithApplication_EmitsAppTypeTotalOnly()
    {
        var counters = ClientEventKeys.Build(ClientEventType.Event, name: "checkout_started", value: null, application: "shop", hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "clientevent-app:shop:total:event:count" && c.Value == 1);

        // Per-app carries the type total only — no per-name / vital under the app prefix.
        counters.Where(c => c.Key.StartsWith("clientevent-app:", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Parsers_RoundTrip()
    {
        ClientEventKeys.TryParseTypeTotal("clientevent:total:error:count", out var t1).ShouldBeTrue();
        t1.ShouldBe("error");

        ClientEventKeys.TryParseTypeHistory("clientevent:total:log:hist:2026-07-26-08", out var t2, out var hour).ShouldBeTrue();
        t2.ShouldBe("log");
        hour.ShouldBe("2026-07-26-08");

        ClientEventKeys.TryParseNameTotal("clientevent:name:event:checkout:count", out var t3, out var name).ShouldBeTrue();
        t3.ShouldBe("event");
        name.ShouldBe("checkout");

        ClientEventKeys.TryParseVital("clientevent:vital:LCP:dur", out var vname, out var token).ShouldBeTrue();
        vname.ShouldBe("LCP");
        token.ShouldBe("dur");

        ClientEventKeys.TryParseVitalPct("clientevent:vital:LCP:pct:2500", out var pname, out var upper).ShouldBeTrue();
        pname.ShouldBe("LCP");
        upper.ShouldBe(2500);

        ClientEventKeys.TryParseAppTypeTotal("clientevent-app:shop:total:event:count", out var app, out var t4).ShouldBeTrue();
        app.ShouldBe("shop");
        t4.ShouldBe("event");
    }

    [Fact]
    public void Parsers_RejectWrongFamilyAndForeignKeys()
    {
        // App key is not an app-agnostic total, and vice-versa.
        ClientEventKeys.TryParseTypeTotal("clientevent-app:shop:total:event:count", out _).ShouldBeFalse();
        ClientEventKeys.TryParseAppTypeTotal("clientevent:total:error:count", out _, out _).ShouldBeFalse();

        // A name-total is not a vital, and a type-total is not a name-total.
        ClientEventKeys.TryParseVital("clientevent:name:error:TypeError:count", out _, out _).ShouldBeFalse();
        ClientEventKeys.TryParseNameTotal("clientevent:total:error:count", out _, out _).ShouldBeFalse();

        // Foreign families (other Warp counter prefixes) are rejected outright.
        ClientEventKeys.TryParseTypeTotal("qwait:default:count", out _).ShouldBeFalse();
        ClientEventKeys.TryParseVital("endpoint:GET /x:success", out _, out _).ShouldBeFalse();
        ClientEventKeys.TryParseAppTypeTotal("clientevent:total:error:count", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void NormalizeVitalValue_ScalesOnlyCls()
    {
        ClientEventKeys.NormalizeVitalValue("CLS", 0.2).ShouldBe(200);
        ClientEventKeys.NormalizeVitalValue("cls", 0.2).ShouldBe(200);   // case-insensitive
        ClientEventKeys.NormalizeVitalValue("LCP", 2400).ShouldBe(2400); // untouched
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(50, 50)]
    [InlineData(51, 100)]
    [InlineData(2400, 2500)]
    [InlineData(99999, int.MaxValue)]
    public void BucketFor_AssignsSmallestBoundAtOrAbove(int valueMs, int expectedBucket)
    {
        ClientEventKeys.BucketFor(valueMs).ShouldBe(expectedBucket);
    }

    [Fact]
    public void Sanitize_ReplacesColonWithDash()
    {
        ClientEventKeys.Sanitize("a:b:c").ShouldBe("a-b-c");
    }
}
