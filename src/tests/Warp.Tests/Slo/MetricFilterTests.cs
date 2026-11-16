using Shouldly;
using Warp.Core.Slo;

namespace Warp.Tests.Slo;

[Trait("Category", "NoDb")]
public class MetricFilterTests
{
    // The shared page / SLO matching table (W2): (tag key, pattern, canonical stored tags, expected). The vitest
    // `matchesPattern` table in src/ui/src/pages/metrics/metricModel.test.ts mirrors these rows exactly.
    public static TheoryData<string, string, bool> MatchingTable => new()
    {
        { "500", "-", false },
        { "500", "status=500", true },
        { "500", "status=5000", false },
        { "5*", "status=503", true },
        { "5*", "status=5", true },
        { "5*", "status=403", false },
        { "5*", "-", false },
        { "*", "status=200", true },
        { "*", "status=", true },
        { "*", "-", false },
        { "*", "other=x", false },
        { "{none}", "-", true },
        { "{none}", "other=x", true },
        { "{none}", "status=200", false },
        { "{no*", "-", false },
        { "{none}", "status={none}", true },
    };

    [TimedTheory]
    [MemberData(nameof(MatchingTable))]
    public void Matches_PageAndSloFilterForms_AgreeOnTheSharedTable(string pattern, string canonical, bool expected)
    {
        var tags = MetricFilter.ParseCanonicalTags(canonical);
        var slo = Parse($"status={pattern}");
        MetricFilter.TryCreate(new Dictionary<string, string> { ["status"] = pattern }, out var page, out var error).ShouldBeTrue(error);

        slo.Matches(tags).ShouldBe(expected);
        page.Matches(tags).ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("", "500")]
    [InlineData("a*", "500")]
    [InlineData("status", "x*y")]
    [InlineData("status", "*x")]
    public void TryCreate_InvalidKeyOrValue_ReportsError(string key, string value)
    {
        MetricFilter.TryCreate(new Dictionary<string, string> { [key] = value }, out var filter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        filter.IsEmpty.ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_EmptyFilter_MatchesEverything(string? text)
    {
        var filter = Parse(text);

        filter.IsEmpty.ShouldBeTrue();
        filter.Matches(Tags()).ShouldBeTrue();
        filter.Matches(Tags(("a", "b"))).ShouldBeTrue();
    }

    [TimedFact]
    public void Matches_ExactValue_ComparesOrdinally()
    {
        var filter = Parse("status=500");

        filter.Matches(Tags(("status", "500"))).ShouldBeTrue();
        filter.Matches(Tags(("status", "5000"))).ShouldBeFalse();
        filter.Matches(Tags(("status", "STATUS"))).ShouldBeFalse();
        Parse("route=/Orders").Matches(Tags(("route", "/orders"))).ShouldBeFalse();
    }

    [TimedFact]
    public void Matches_MultipleClauses_AreAndedAndTrimmed()
    {
        var filter = Parse(" status = 5* , route=/orders ");

        filter.Matches(Tags(("status", "503"), ("route", "/orders"))).ShouldBeTrue();
        filter.Matches(Tags(("status", "503"), ("route", "/other"))).ShouldBeFalse();
        filter.Matches(Tags(("status", "200"), ("route", "/orders"))).ShouldBeFalse();
    }

    [TimedFact]
    public void Matches_TrailingStar_IsPrefixMatch()
    {
        var filter = Parse("status=5*");

        filter.Matches(Tags(("status", "5"))).ShouldBeTrue();
        filter.Matches(Tags(("status", "503"))).ShouldBeTrue();
        filter.Matches(Tags(("status", "403"))).ShouldBeFalse();
        filter.Matches(Tags(("status", "x5"))).ShouldBeFalse();
    }

    [TimedFact]
    public void Matches_BareStar_MatchesAnyPresentValueButNotAbsent()
    {
        var filter = Parse("region=*");

        filter.Matches(Tags(("region", "eu"))).ShouldBeTrue();
        filter.Matches(Tags(("region", string.Empty))).ShouldBeTrue();
        filter.Matches(Tags(("other", "x"))).ShouldBeFalse();
    }

    [TimedFact]
    public void Matches_AbsentTag_OnlyMatchesNoneLiteral()
    {
        Parse("region={none}").Matches(Tags(("other", "x"))).ShouldBeTrue();
        Parse("region={none}").Matches(Tags()).ShouldBeTrue();
        Parse("region={none}").Matches(Tags(("region", "eu"))).ShouldBeFalse();

        Parse("region=eu").Matches(Tags()).ShouldBeFalse();
        Parse("region={no*").Matches(Tags()).ShouldBeFalse();
        Parse("region=*").Matches(Tags()).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("a", "missing '='")]
    [InlineData("a=1,b", "missing '='")]
    [InlineData("=1", "empty key")]
    [InlineData(" =1", "empty key")]
    [InlineData("a=1,a=2", "more than once")]
    [InlineData("a=1,,b=2", "empty entry")]
    [InlineData("a=1,", "empty entry")]
    [InlineData("a=x*y", "last character")]
    [InlineData("a=*x", "last character")]
    [InlineData("a=x**", "last character")]
    [InlineData("a*=1", "must not contain")]
    public void TryParse_InvalidFilter_ReportsError(string text, string expectedFragment)
    {
        MetricFilter.TryParse(text, out var filter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain(expectedFragment);
        filter.IsEmpty.ShouldBeTrue();
    }

    [TimedFact]
    public void Matches_ValueContainingEquals_MatchesItLiterally()
    {
        var filter = Parse("q=a=b");

        filter.Matches(Tags(("q", "a=b"))).ShouldBeTrue();
    }

    [TimedFact]
    public void ValueListMatches_ExactAndPrefixValues_MatchAsListed()
    {
        MetricValueList.TryParse("ok, 2*,cached", out var list, out var error).ShouldBeTrue(error);

        list.Matches("ok").ShouldBeTrue();
        list.Matches("cached").ShouldBeTrue();
        list.Matches("200").ShouldBeTrue();
        list.Matches("2").ShouldBeTrue();
        list.Matches("refused").ShouldBeFalse();
        list.Matches("okay").ShouldBeFalse();
        list.Matches(null).ShouldBeFalse();
    }

    [TimedFact]
    public void ValueListMatches_AbsentValue_IsNone()
    {
        MetricValueList.TryParse("{none},x", out var list, out _).ShouldBeTrue();

        list.Matches(null).ShouldBeTrue();
        list.Matches("x").ShouldBeTrue();
        list.Matches("y").ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("a,,b")]
    [InlineData("a,")]
    [InlineData("a*b")]
    public void ValueListTryParse_Invalid_ReportsError(string csv)
    {
        MetricValueList.TryParse(csv, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [TimedFact]
    public void ValueListMatches_EmptyList_MatchesNothing()
    {
        MetricValueList.TryParse(null, out var list, out _).ShouldBeTrue();

        list.Matches("a").ShouldBeFalse();
        list.Patterns.Count.ShouldBe(0);
    }

    [TimedTheory]
    [InlineData("ok,retry", "error,retry")] // equal literals
    [InlineData("500", "5*")] // literal under the other side's prefix
    [InlineData("5*", "500")]
    [InlineData("5*", "50*")] // nested prefixes, both ways
    [InlineData("50*", "5*")]
    [InlineData("5*", "5*")]
    [InlineData("*", "error")] // a bare * overlaps anything
    [InlineData("ok", "*")]
    [InlineData("*", "{none}")]
    public void OverlapsWith_PatternsThatCanMatchOneValue_ReturnsTrue(string good, string bad)
    {
        MetricValueList.TryParse(good, out var goodList, out _).ShouldBeTrue();
        MetricValueList.TryParse(bad, out var badList, out _).ShouldBeTrue();

        goodList.OverlapsWith(badList).ShouldBeTrue();
        badList.OverlapsWith(goodList).ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData("2*,3*", "5*")]
    [InlineData("ok", "error")]
    [InlineData("500", "5")]
    [InlineData("50*", "51*")]
    [InlineData("5", "50*")]
    public void OverlapsWith_DisjointPatterns_ReturnsFalse(string good, string bad)
    {
        MetricValueList.TryParse(good, out var goodList, out _).ShouldBeTrue();
        MetricValueList.TryParse(bad, out var badList, out _).ShouldBeTrue();

        goodList.OverlapsWith(badList).ShouldBeFalse();
        badList.OverlapsWith(goodList).ShouldBeFalse();
    }

    [TimedFact]
    public void ParseCanonicalTags_Pairs_ReturnsThem()
    {
        var tags = MetricFilter.ParseCanonicalTags("outcome=ok,route=/a/b");

        tags.Count.ShouldBe(2);
        tags["outcome"].ShouldBe("ok");
        tags["route"].ShouldBe("/a/b");
    }

    [TimedTheory]
    [InlineData("-")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseCanonicalTags_EmptyForms_ReturnEmpty(string? canonical)
    {
        MetricFilter.ParseCanonicalTags(canonical).ShouldBeEmpty();
    }

    [TimedFact]
    public void Matches_ParsedCanonicalTags_FeedFilterMatching()
    {
        var tags = MetricFilter.ParseCanonicalTags("status=503,region=eu");

        Parse("status=5*,region=eu").Matches(tags).ShouldBeTrue();
        Parse("zone={none}").Matches(tags).ShouldBeTrue();
        Parse("status=2*").Matches(tags).ShouldBeFalse();
        Parse("status=5*").Matches(MetricFilter.ParseCanonicalTags("-")).ShouldBeFalse();
    }

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static MetricFilter Parse(string? text)
    {
        MetricFilter.TryParse(text, out var filter, out var error).ShouldBeTrue(error);

        return filter;
    }
}
