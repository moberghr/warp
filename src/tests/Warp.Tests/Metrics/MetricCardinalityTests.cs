using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Metrics;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricCardinalityGuard"/>: the tag-value (R7), series (R8) and instrument (R9)
/// caps, warn-once reporting that never includes a tag value (§1.2), the <c>warp.metrics.collapsed</c> meter,
/// the collapse snapshot storage reads, and the cap holding under a race.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricCardinalityTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void MapValue_BeyondMaxValuesPerTag_CollapsesToOtherAndWarnsOnceWithoutValue()
    {
        var logs = new CapturingLoggerProvider();
        var cardinality = CreateGuard(new WarpMetricsOptions { MaxValuesPerTag = 2 }, logs).CreateInstrument(Info("values.instrument"), null);

        cardinality.MapValue("customer", "alpha").ShouldBe("alpha");
        cardinality.MapValue("customer", "beta").ShouldBe("beta");
        cardinality.MapValue("customer", "secret-gamma").ShouldBe(MetricSeries.Other);
        cardinality.MapValue("customer", "secret-delta").ShouldBe(MetricSeries.Other);
        cardinality.MapValue("customer", "alpha").ShouldBe("alpha");

        Warnings(logs).ShouldHaveSingleItem();
        Warnings(logs)[0].ShouldContain("values.instrument");
        Warnings(logs)[0].ShouldContain("customer");
        Warnings(logs)[0].ShouldNotContain("secret");
    }

    [TimedFact]
    public void AdmitSeries_BeyondMaxSeries_ReturnsOtherSeries()
    {
        var cardinality = CreateGuard(new WarpMetricsOptions { MaxSeriesPerInstrument = 2 }).CreateInstrument(Info("series.instrument"), null);

        cardinality.AdmitSeries("a=1").Tags.ShouldBe("a=1");
        cardinality.AdmitSeries("a=2").Tags.ShouldBe("a=2");
        cardinality.AdmitSeries("a=3").Tags.ShouldBe(MetricSeries.Other);
        cardinality.AdmitSeries("a=1").Tags.ShouldBe("a=1");

        cardinality.SeriesCount.ShouldBe(2);
    }

    [TimedFact]
    public void TryAdmitInstrument_BeyondMaxInstruments_RefusesNewAndReadmitsKnown()
    {
        var guard = CreateGuard(new WarpMetricsOptions { MaxInstruments = 1 });

        guard.TryAdmitInstrument(Info("first")).ShouldBeTrue();
        guard.TryAdmitInstrument(Info("second")).ShouldBeFalse();
        guard.TryAdmitInstrument(Info("first")).ShouldBeTrue();

        var collapse = guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(MetricSeries.Other);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonInstrumentCap]);
    }

    [TimedFact]
    public void TryAdmitInstrument_FiveHundredBeyondTheCap_KeepsBoundedStateAndWarnsOnce()
    {
        var logs = new CapturingLoggerProvider();
        var guard = CreateGuard(new WarpMetricsOptions { MaxInstruments = 2 }, logs);

        var admitted = Enumerable.Range(0, 502)
            .Count(x => guard.TryAdmitInstrument(Info($"cap.instrument.{x}")));

        admitted.ShouldBe(2);
        guard.InstrumentCount.ShouldBe(2);
        guard.WarningCount.ShouldBe(0);
        guard.Collapsed().ShouldHaveSingleItem().Instrument.ShouldBe(MetricSeries.Other);
        Warnings(logs).ShouldHaveSingleItem().ShouldContain(MetricCardinalityGuard.ReasonInstrumentCap);
    }

    [TimedFact]
    public void TryAdmitInstrument_SameNameFromAnotherMeterOrShape_IsRefusedAndReportedOnce()
    {
        var logs = new CapturingLoggerProvider();
        var guard = CreateGuard(new WarpMetricsOptions(), logs);
        var first = new MetricInstrumentInfo("shared", "meter.one", MetricKind.Histogram, null, null, [10, 100], false);

        guard.TryAdmitInstrument(first).ShouldBeTrue();
        guard.TryAdmitInstrument(new MetricInstrumentInfo("shared", "meter.two", MetricKind.Histogram, null, null, [10, 100], false)).ShouldBeFalse();
        guard.TryAdmitInstrument(new MetricInstrumentInfo("shared", "meter.one", MetricKind.Histogram, null, null, [5], false)).ShouldBeFalse();
        guard.TryAdmitInstrument(new MetricInstrumentInfo("shared", "meter.one", MetricKind.Counter, null, null, null, false)).ShouldBeFalse();
        guard.TryAdmitInstrument(new MetricInstrumentInfo("shared", "meter.one", MetricKind.Histogram, null, null, [10, 100], false)).ShouldBeTrue();

        guard.Collapsed().ShouldHaveSingleItem().Reasons.ShouldBe([MetricCardinalityGuard.ReasonNameConflict]);
        var warning = Warnings(logs).ShouldHaveSingleItem();
        warning.ShouldContain("shared");
        warning.ShouldContain("meter.one");
        warning.ShouldContain("meter.two");
    }

    [TimedFact]
    public void Resolve_ThousandDistinctDynamicTagKeys_TracksAtMostTheKeyCapAndWarnsOnce()
    {
        var options = new WarpMetricsOptions();
        var logs = new CapturingLoggerProvider();
        var guard = CreateGuard(options, logs);
        var cardinality = guard.CreateInstrument(Info("dynamic.keys"), null);

        for (var i = 0; i < 1_000; i++)
        {
            MetricSeries.Resolve([new KeyValuePair<string, object?>($"secret-key-{i}", "v")], cardinality, null, options.MaxTagsPerInstrument);
        }

        cardinality.TagKeyCount.ShouldBe(options.MaxTagsPerInstrument * 2);
        guard.WarningCount.ShouldBe(1);
        var warning = Warnings(logs).ShouldHaveSingleItem();
        warning.ShouldContain(MetricCardinalityGuard.ReasonTagCount);
        warning.ShouldNotContain("secret-key");
        var collapse = guard.Collapsed().ShouldHaveSingleItem();
        collapse.TagKeys.ShouldBeEmpty();
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonTagCount]);
    }

    [TimedFact]
    public void Collapsed_AfterTagValueCollapse_ListsTagKeyReasonAndTime()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var guard = new MetricCardinalityGuard(new WarpMetricsOptions { MaxValuesPerTag = 1 }, time, LoggerFor(new CapturingLoggerProvider()));
        var cardinality = guard.CreateInstrument(Info("snapshot.instrument"), null);

        cardinality.MapValue("tenant", "a");
        cardinality.MapValue("tenant", "b");

        var collapse = guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe("snapshot.instrument");
        collapse.TagKeys.ShouldBe(["tenant"]);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonTagValues]);
        collapse.LastCollapsedAt.ShouldBe(now.UtcDateTime);
    }

    [TimedFact]
    public void Report_IncrementsCollapsedMeterWithInstrumentAndReason()
    {
        var instrument = $"meter.{Guid.NewGuid():N}";
        var reasons = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (x, y) =>
            {
                if (MetricCollapseCountingTests.IsCollapsedMeter(x))
                {
                    y.EnableMeasurementEvents(x);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tagList = tags.ToArray();
            if (tagList.Any(x => string.Equals(x.Key, "instrument", StringComparison.Ordinal) && Equals(x.Value, instrument)))
            {
                reasons.Add((string)tagList.Single(x => string.Equals(x.Key, "reason", StringComparison.Ordinal)).Value!);
            }
        });
        listener.Start();

        CreateGuard(new WarpMetricsOptions()).Report(instrument, "k", MetricCardinalityGuard.ReasonSeries);

        reasons.ShouldBe([MetricCardinalityGuard.ReasonSeries]);
    }

    [TimedFact]
    public async Task MapValue_NewValueAdmittedWhileAnotherMissedTheLookup_AdmitsExactlyOneAtCap()
    {
        var cardinality = CreateGuard(new WarpMetricsOptions { MaxValuesPerTag = 2 }).CreateInstrument(Info("race.instrument"), null);
        cardinality.MapValue("k", "seed");

        var barrier = new BarrierSignal();
        var blocked = 0;
        cardinality.BeforeValueAdmission = () =>
        {
            // Pin only the first caller: it has missed the lock-free lookup and not yet taken the admission lock.
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                barrier.Running.Release();
                barrier.CanFinish.Wait(TimeSpan.FromSeconds(5), Ct).ShouldBeTrue();
            }
        };
        var pinned = Task.Run(() => cardinality.MapValue("k", "new-a"), Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var admitted = cardinality.MapValue("k", "new-b");
        barrier.CanFinish.Release();
        var late = await pinned;

        admitted.ShouldBe("new-b");
        late.ShouldBe(MetricSeries.Other);
    }

    [TimedFact]
    public void Resolve_FuzzTenThousandRandomValuesOverSixTags_StaysWithinValueBoundAndWarnsOncePerKey()
    {
        var options = new WarpMetricsOptions();
        var logs = new CapturingLoggerProvider();
        var cardinality = CreateGuard(options, logs).CreateInstrument(Info("fuzz.values"), "app");

        var generated = new HashSet<string>(StringComparer.Ordinal);

        var seen = Fuzz(cardinality, options.MaxTagsPerInstrument, generated);

        seen.Count.ShouldBeLessThanOrEqualTo(options.MaxSeriesPerInstrument + 1);

        // Per key, at most MaxValuesPerTag real values plus the {other} bucket ever reach a series.
        var valuesPerKey = seen
            .Where(x => !string.Equals(x, MetricSeries.Other, StringComparison.Ordinal))
            .SelectMany(x => x.Split(','))
            .Select(x => x.Split('=', 2))
            .GroupBy(x => x[0], x => x[1], StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        valuesPerKey.Keys.Order(StringComparer.Ordinal).ShouldBe(FuzzKeys);
        valuesPerKey.Values.ShouldAllBe(x => x <= options.MaxValuesPerTag + 1);

        Warnings(logs).Count(x => x.Contains(MetricCardinalityGuard.ReasonTagValues, StringComparison.Ordinal)).ShouldBe(FuzzKeys.Length);

        // §1.2: no value the fuzz generated appears in any warning, not merely no "=v" fragment.
        generated.Count.ShouldBeGreaterThan(options.MaxValuesPerTag);
        Warnings(logs).ShouldAllBe(x => !generated.Any(y => x.Contains(y, StringComparison.Ordinal)));
    }

    [TimedFact]
    public void Resolve_FuzzTenThousandRandomValuesOverSixTags_StaysWithinSeriesBoundAndWarnsOnce()
    {
        // Values uncapped, so only the series cap bounds the space: MaxSeriesPerInstrument real series + {other}.
        var options = new WarpMetricsOptions { MaxValuesPerTag = 100_000 };
        var logs = new CapturingLoggerProvider();
        var cardinality = CreateGuard(options, logs).CreateInstrument(Info("fuzz.series"), "app");

        var seen = Fuzz(cardinality, options.MaxTagsPerInstrument);

        seen.Count.ShouldBeLessThanOrEqualTo(options.MaxSeriesPerInstrument + 1);
        cardinality.SeriesCount.ShouldBe(options.MaxSeriesPerInstrument);
        Warnings(logs).ShouldHaveSingleItem().ShouldContain(MetricCardinalityGuard.ReasonSeries);
    }

    private static readonly string[] FuzzKeys = ["k1", "k2", "k3", "k4", "k5", "k6"];

    private static HashSet<string> Fuzz(MetricInstrumentCardinality cardinality, int maxTags, HashSet<string>? generated = null)
    {
        var random = new Random(20261113);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++)
        {
            var tags = FuzzKeys
                .Select(x => new KeyValuePair<string, object?>(x, $"v{random.Next(1_000_000)}"))
                .ToArray();
            generated?.UnionWith(tags.Select(x => (string)x.Value!));

            seen.Add(MetricSeries.Resolve(tags, cardinality, null, maxTags).Tags);
        }

        return seen;
    }

    private static MetricCardinalityGuard CreateGuard(WarpMetricsOptions options, CapturingLoggerProvider? logs = null)
        => new(options, TimeProvider.System, LoggerFor(logs ?? new CapturingLoggerProvider()));

    private static ILogger<MetricCardinalityGuard> LoggerFor(CapturingLoggerProvider logs)
        => new CapturingLoggerFactory(logs).CreateLogger<MetricCardinalityGuard>();

    private static List<string> Warnings(CapturingLoggerProvider logs)
        => [.. logs.Logs.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message)];

    private static MetricInstrumentInfo Info(string name)
        => new(name, "test.meter", MetricKind.Counter, null, null, null, false);
}
