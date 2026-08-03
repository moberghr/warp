using Shouldly;
using Warp.Core.Logging;
using Warp.Core.Services;

namespace Warp.Tests.Observability;

/// <summary>
/// NoDb coverage for the dropped-record key helpers — the durable <c>warpsys:records-dropped</c> series that makes
/// a saturated lossy pipeline visible in-box. Keys must be classifiable by the generic <see cref="MetricTiers"/>
/// reader/rollup so they age like every other tiered stat.
/// </summary>
[Trait("Category", "NoDb")]
public class DroppedRecordKeysTests
{
    [Theory]
    [InlineData(DropPipeline.Adapter, "adapter")]
    [InlineData(DropPipeline.Endpoint, "endpoint")]
    [InlineData(DropPipeline.Client, "client")]
    public void Token_MapsPipeline(DropPipeline pipeline, string token)
        => DroppedRecordKeys.Token(pipeline).ShouldBe(token);

    [Fact]
    public void History_IsBasePlusSuffix_AndClassifiableByTiers()
    {
        var suffix = MetricTiers.Suffix(MetricTier.Fine, new DateTime(2026, 8, 2, 14, 5, 0, DateTimeKind.Utc), 5);
        var key = DroppedRecordKeys.History(DropPipeline.Adapter, suffix);

        key.ShouldStartWith("warpsys:records-dropped:adapter:");
        MetricTiers.TryClassifyKey(key, out var baseKey, out var tier, out _).ShouldBeTrue();
        baseKey.ShouldBe("warpsys:records-dropped:adapter");
        tier.ShouldBe(MetricTier.Fine);
    }
}
