using System.Text.RegularExpressions;
using Shouldly;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// The server's list of the metric page's query keys (<see cref="MetricChartQuery.MetricQueryKeys"/>, which a chart tile's
/// stored query is validated against) equals the dashboard's own <c>metricQueryKeys</c>, read from
/// <c>src/ui/src/pages/metrics/metricModel.ts</c>: a control the page starts writing without the server learning it would
/// make every "Add to board" with that control a 400, and a key the server accepts but the page never reads is a
/// parameter a tile could smuggle into a link. The range keys are checked the same way (<c>METRIC_RANGE_KEYS</c>).
/// </summary>
[Trait("Category", "NoDb")]
public partial class MetricChartQueryKeysSyncTests
{
    [TimedFact]
    public void MetricQueryKeys_EqualTheDashboardsList()
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), "metrics", "metricModel.ts"));

        var keys = ReadConstArray(source, "metricQueryKeys");

        MetricChartQuery.MetricQueryKeys.ShouldBe(keys);
    }

    [TimedFact]
    public void RangeKeys_EqualTheDashboardsList()
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), "metrics", "metricModel.ts"));

        var keys = ReadConstArray(source, "METRIC_RANGE_KEYS");

        MetricChartQuery.RangeKeys.ShouldBe(keys);
    }

    // `export const name = [ 'a', 'b', ... ] as const;` → ["a", "b", ...], in order.
    private static List<string> ReadConstArray(string source, string constant)
    {
        var match = Regex.Match(source, $@"export const {constant}\s*=\s*\[(?<body>[^\]]*)\]\s*as const", RegexOptions.None, TimeSpan.FromSeconds(1));
        match.Success.ShouldBeTrue($"'export const {constant} = [...] as const' not found");

        return [.. QuotedString().Matches(match.Groups["body"].Value).Select(x => x.Groups["value"].Value)];
    }

    // The repo's src/ui/src/pages, found by walking up from the test binaries (bin/<config>/<tfm> under src/tests).
    private static string PagesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ui", "src", "pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/ui/src/pages was not found above the test binaries.");
    }

    [GeneratedRegex("'(?<value>[^']*)'", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedString();
}
