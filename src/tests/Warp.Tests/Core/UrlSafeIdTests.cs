using Shouldly;
using Warp.Core.Models;

namespace Warp.Tests.Core;

// The dashboard addresses three things by an arbitrary string identity — an endpoint route, an
// application name, and (since names replaced surrogate ids on IRecurringJobService) a recurring
// job name. All three travel as one path segment through this codec, so a name holding '/', a
// space, or non-ASCII has to survive the round-trip exactly; a lossy encode would silently
// address a DIFFERENT definition, or none.
[Trait("Category", "NoDb")]
public class UrlSafeIdTests
{
    [Theory]
    [InlineData("nightly-report")]
    [InlineData("Daily Report")]
    [InlineData("tenant/acme/sync")]
    [InlineData("GET /orders/{id}")]
    [InlineData("naplata-računa")]
    [InlineData("a")]
    public void TryDecode_OfEncode_ReturnsOriginal(string value)
    {
        UrlSafeId.TryDecode(UrlSafeId.Encode(value)).ShouldBe(value);
    }

    [Fact]
    public void Encode_ProducesNoPathOrQuerySeparators()
    {
        var encoded = UrlSafeId.Encode("tenant/acme/sync?x=1");

        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("=");
    }

    [Fact]
    public void TryDecode_MalformedId_ReturnsNull()
    {
        // A hand-typed or stale route segment must read as "not found", never throw.
        UrlSafeId.TryDecode("!!!not-base64!!!").ShouldBeNull();
    }
}
