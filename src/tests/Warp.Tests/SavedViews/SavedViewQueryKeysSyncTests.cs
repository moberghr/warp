using System.Text.RegularExpressions;
using Shouldly;
using Warp.Core.SavedViews;

namespace Warp.Tests.SavedViews;

/// <summary>
/// The C# query-key allowlists in <see cref="SavedViewValidation"/> equal the dashboard's own lists, read from the TS
/// model files in the repo: a key the UI starts writing without the server learning it would make every view saved on
/// that page a 400, and a key the server accepts but the UI never reads is a parameter a view could smuggle in.
/// </summary>
[Trait("Category", "NoDb")]
public partial class SavedViewQueryKeysSyncTests
{
    [TimedTheory]
    [InlineData("metrics/metricModel.ts", "metricQueryKeys", SavedViewValidation.MetricPage)]
    [InlineData("issues/issuesModel.ts", "issuesQueryKeys", SavedViewValidation.IssuesPage)]
    [InlineData("jobs/jobsModel.ts", "jobsQueryKeys", SavedViewValidation.JobsPage)]
    [InlineData("jobs/jobsModel.ts", "jobsByTypeQueryKeys", SavedViewValidation.JobsByTypePage)]
    public void QueryKeys_EqualTheDashboardsList(string file, string constant, string page)
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), file));

        var keys = ReadConstArray(source, constant);

        SavedViewValidation.QueryKeys(page).ShouldNotBeNull().ShouldBe(keys);
    }

    [TimedFact]
    public void Pages_EqualTheDashboardsSavedViewPages()
    {
        var source = File.ReadAllText(Path.Combine(PagesDirectory(), "..", "lib", "savedViewsModel.ts"));

        ReadConstArray(source, "SAVED_VIEW_PAGES").ShouldBe(SavedViewValidation.Pages);
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
