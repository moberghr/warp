using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricSeries"/>: the canonical tag set (allowlist projection, ordinal key order,
/// tag-count cap), value sanitization and invariant formatting, series identity (R4) and interning.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricSeriesTests
{
    [TimedFact]
    public void Resolve_UnsortedTags_SortsByKeyInOrdinalOrder()
    {
        var cardinality = CreateCardinality();

        var series = MetricSeries.Resolve(Tags(("b", "2"), ("a", "1"), ("B", "3")), cardinality, null, 6);

        series.Tags.ShouldBe("B=3,a=1,b=2");
    }

    [TimedFact]
    public void Resolve_NoTags_ReturnsDash()
    {
        var series = MetricSeries.Resolve([], CreateCardinality(), null, 6);

        series.Tags.ShouldBe(MetricSeries.NoTags);
    }

    [TimedFact]
    public void Resolve_WithAllowlist_DropsUnlistedKeys()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { "route" };

        var series = MetricSeries.Resolve(Tags(("route", "/orders/{id}"), ("url.full", "https://x/orders/42")), CreateCardinality(), allowlist, 6);

        series.Tags.ShouldBe("route=/orders/{id}");
    }

    [TimedFact]
    public void Resolve_AllTagsFilteredOut_ReturnsDash()
    {
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { "route" };

        var series = MetricSeries.Resolve(Tags(("other", "x")), CreateCardinality(), allowlist, 6);

        series.Tags.ShouldBe(MetricSeries.NoTags);
    }

    [TimedFact]
    public void Resolve_MoreKeysThanMax_DropsExtraKeysInOrdinalOrderAndReports()
    {
        var guard = CreateGuard();
        var cardinality = guard.CreateInstrument(Info("tagcount.instrument"), null);

        var series = MetricSeries.Resolve(Tags(("c", "3"), ("a", "1"), ("b", "2")), cardinality, null, 2);

        series.Tags.ShouldBe("a=1,b=2");
        var collapse = guard.Collapsed().ShouldHaveSingleItem();
        collapse.TagKeys.ShouldBe(["c"]);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonTagCount]);
    }

    [TimedFact]
    public void Resolve_MoreKeysThanMaxRepeatedly_ReportsTheDroppedKeyOnlyOnce()
    {
        var start = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var guard = new MetricCardinalityGuard(new WarpMetricsOptions(), time, NullLogger<MetricCardinalityGuard>.Instance);
        var cardinality = guard.CreateInstrument(Info("tagcount.steady"), null);
        MetricSeries.Resolve(Tags(("c", "3"), ("a", "1"), ("b", "2")), cardinality, null, 2);
        time.Advance(TimeSpan.FromMinutes(1));

        MetricSeries.Resolve(Tags(("c", "3"), ("a", "1"), ("b", "2")), cardinality, null, 2);

        guard.Collapsed().ShouldHaveSingleItem().LastCollapsedAt.ShouldBe(start.UtcDateTime);
    }

    [TimedFact]
    public void Resolve_KeysThatSanitizeAlike_SortAndDeduplicateOnTheSanitizedKey()
    {
        var series = MetricSeries.Resolve(Tags(("\u0001z", "2"), ("a,b", "1"), ("a-b", "3")), CreateCardinality(), null, 6);

        series.Tags.ShouldBe("a-b=1,z=2");
    }

    [TimedFact]
    public void Resolve_KeysThatSanitizeAlike_ShareOneValueMap()
    {
        var cardinality = CreateGuard().CreateInstrument(Info($"series.{Guid.NewGuid():N}"), null);

        MetricSeries.Resolve(Tags(("a,b", "1")), cardinality, null, 6);
        MetricSeries.Resolve(Tags(("a=b", "2")), cardinality, null, 6);

        cardinality.TagKeyCount.ShouldBe(1);
    }

    [TimedFact]
    public void Resolve_DuplicateKey_KeepsOneEntry()
    {
        var series = MetricSeries.Resolve(Tags(("a", "1"), ("a", "1")), CreateCardinality(), null, 6);

        series.Tags.ShouldBe("a=1");
    }

    [TimedFact]
    public void Resolve_SameTagsTwice_ReturnsTheInternedInstance()
    {
        var cardinality = CreateCardinality();

        var first = MetricSeries.Resolve(Tags(("a", "1"), ("b", "2")), cardinality, null, 6);
        var second = MetricSeries.Resolve(Tags(("b", "2"), ("a", "1")), cardinality, null, 6);

        second.ShouldBeSameAs(first);
    }

    [TimedFact]
    public void Resolve_TagNamedApplication_DoesNotSetApplication()
    {
        var cardinality = CreateGuard().CreateInstrument(Info("app.instrument"), "orders");

        var series = MetricSeries.Resolve(Tags(("application", "spoofed")), cardinality, null, 6);

        series.Application.ShouldBe("orders");
        series.Tags.ShouldBe("application=spoofed");
    }

    [TimedFact]
    public void Equals_SameIdentityDifferentInstances_AreEqual()
    {
        var info = Info("eq.instrument");
        var one = new MetricSeries(info, "app", "a=1");
        var two = new MetricSeries(info, "app", "a=1");
        var otherApp = new MetricSeries(info, null, "a=1");

        one.ShouldBe(two);
        one.GetHashCode().ShouldBe(two.GetHashCode());
        one.ShouldNotBe(otherApp);
    }

    [TimedFact]
    public void Equals_SameSeriesOfAnotherVersion_IsAnotherSeries()
    {
        var info = Info("eq.version");
        var one = new MetricSeries(info, "app", "a=1", "1.0.0");
        var two = new MetricSeries(info, "app", "a=1", "1.0.0");
        var otherVersion = new MetricSeries(info, "app", "a=1", "2.0.0");
        var noVersion = new MetricSeries(info, "app", "a=1");

        one.ShouldBe(two);
        one.GetHashCode().ShouldBe(two.GetHashCode());
        one.ShouldNotBe(otherVersion);
        one.ShouldNotBe(noVersion);
    }

    [TimedFact]
    public void CreateInstrument_WithVersion_StampsItOnEveryAdmittedSeries()
    {
        var cardinality = CreateGuard().CreateInstrument(Info($"version.{Guid.NewGuid():N}"), "orders", "2.4.1");

        var series = MetricSeries.Resolve(Tags(("a", "1")), cardinality, null, 6);

        series.Version.ShouldBe("2.4.1");
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData("2.4.1")]
    public void TryToPoint_Series_CopiesItsVersionOrLeavesItNull(string? version)
    {
        var aggregator = new MetricAggregator(TimeProvider.System, 5);
        aggregator.Record(new MetricSeries(Info("version.point"), "orders", MetricSeries.NoTags, version), 1);

        MetricPointMapper.TryToPoint(aggregator.Drain().ShouldHaveSingleItem(), out var point).ShouldBeTrue();

        point.Version.ShouldBe(version);
        point.Application.ShouldBe("orders");
    }

    [TimedTheory]
    [InlineData(null, "{none}")]
    [InlineData("", "{none}")]
    [InlineData("   ", "{none}")]
    [InlineData("  padded  ", "padded")]
    [InlineData("a,b=c", "a-b-c")]
    [InlineData("line\nbreak\ttab", "linebreaktab")]
    public void FormatValue_String_Sanitizes(string? input, string expected)
    {
        Format(input).ShouldBe(expected);
    }

    [TimedFact]
    public void FormatValue_LongString_TruncatesTo64()
    {
        var formatted = Format(new string('x', 100));

        formatted.Length.ShouldBe(MetricSeries.MaxValueLength);
    }

    [TimedFact]
    public void FormatValue_NumbersAndBools_UseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Format(1.5).ShouldBe("1.5");
            Format(1234567).ShouldBe("1234567");
            Format(true).ShouldBe("True");
            Format(404L).ShouldBe("404");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TimedFact]
    public void Resolve_TagSetLongerThanTheColumn_DropsTrailingTagsAndReportsTheirKeysOnce()
    {
        // Six maximal tags (64-char key and value) are 6 × 129 + 5 characters; only three fit in 512.
        const string secret = "customer-top-secret";
        var guard = CreateGuard();
        var cardinality = guard.CreateInstrument(Info("tags.truncated"), null);
        var tags = Enumerable.Range(0, 6)
            .Select(x => new KeyValuePair<string, object?>($"k{x}".PadRight(MetricSeries.MaxValueLength, 'k'), $"{secret}-{x}".PadRight(MetricSeries.MaxValueLength, 'v')))
            .ToArray();

        var series = MetricSeries.Resolve(tags, cardinality, null, 6);
        MetricSeries.Resolve(tags, cardinality, null, 6).ShouldBeSameAs(series);

        series.Tags.Length.ShouldBeLessThanOrEqualTo(MetricSeries.MaxTagsLength);
        series.Tags.Split(',').Select(x => x[..2]).ShouldBe(["k0", "k1", "k2"]);
        var collapse = guard.Collapsed().ShouldHaveSingleItem();
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonTagsTruncated]);
        collapse.TagKeys.Select(x => x[..2]).ShouldBe(["k3", "k4", "k5"]);
        collapse.TagKeys.ShouldAllBe(x => !x.Contains(secret, StringComparison.Ordinal));
        guard.WarningCount.ShouldBe(3);
    }

    private static string Format(object? value)
    {
        Span<char> buffer = stackalloc char[MetricSeries.MaxValueLength];
        var length = MetricSeries.FormatValue(value, buffer);

        return buffer[..length].ToString();
    }

    private static KeyValuePair<string, object?>[] Tags(params (string Key, object? Value)[] tags)
        => [.. tags.Select(x => new KeyValuePair<string, object?>(x.Key, x.Value))];

    private static MetricCardinalityGuard CreateGuard()
        => new(new WarpMetricsOptions(), TimeProvider.System, NullLogger<MetricCardinalityGuard>.Instance);

    private static MetricInstrumentCardinality CreateCardinality()
        => CreateGuard().CreateInstrument(Info($"series.{Guid.NewGuid():N}"), null);

    private static MetricInstrumentInfo Info(string name)
        => new(name, "test.meter", MetricKind.Counter, null, null, null, false);
}
