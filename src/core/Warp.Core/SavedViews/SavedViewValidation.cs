using Warp.Core.Data.Entities;

namespace Warp.Core.SavedViews;

/// <summary>
/// The boundary rules of a saved view (§8.34), pure: the upsert route answers 400 with the message. A view stores a page
/// id plus that page's URL query string, so the rules are the page allowlist, the target each page's route needs, bounded
/// lengths and the query keys each page reads. An unknown key is rejected, so a view can never smuggle an arbitrary
/// parameter into a link. The key lists mirror the dashboard's <c>metricQueryKeys</c> / <c>issuesQueryKeys</c> /
/// <c>jobsQueryKeys</c> / <c>jobsByTypeQueryKeys</c> (<c>src/ui/src/pages/{metrics,issues,jobs}/*Model.ts</c>);
/// <c>SavedViewQueryKeysSyncTests</c> reads those files and fails when the two drift.
/// </summary>
public static class SavedViewValidation
{
    public const int MaxNameLength = 200;

    public const int MaxPageLength = 64;

    public const int MaxTargetLength = 256;

    public const int MaxQueryLength = 2000;

    /// <summary>The most views (personal and shared) one author may own.</summary>
    public const int MaxViewsPerAuthor = 200;

    /// <summary>The most shared views a host may hold.</summary>
    public const int MaxSharedViews = 500;

    /// <summary>The metric detail page, <c>/metrics/{UrlSafeId(instrument)}</c>; the target is the instrument name.</summary>
    public const string MetricPage = "metric";

    /// <summary>The Issues list, <c>/issues</c>; no target.</summary>
    public const string IssuesPage = "issues";

    /// <summary>The Jobs list, <c>/jobs/{state}</c>; the target is the state (the route has no list without one).</summary>
    public const string JobsPage = "jobs";

    /// <summary>Every job of one type, <c>/jobs/by-type/{type}</c>; the target is the job type.</summary>
    public const string JobsByTypePage = "jobs-by-type";

    /// <summary>The pages a view can be saved on.</summary>
    public static IReadOnlyList<string> Pages { get; } = [MetricPage, IssuesPage, JobsPage, JobsByTypePage];

    /// <summary>The metric detail page's keys: <c>metricQueryKeys</c> in <c>pages/metrics/metricModel.ts</c>.</summary>
    public static IReadOnlyList<string> MetricQueryKeys { get; } =
        ["range", "from", "to", "application", "breakdown", "filter", "compare", "percentile", "view", "numerator", "minmax", "rate"];

    /// <summary>The Issues list's keys: <c>issuesQueryKeys</c> in <c>pages/issues/issuesModel.ts</c>.</summary>
    public static IReadOnlyList<string> IssuesQueryKeys { get; } = ["source", "status", "application", "show4xx"];

    /// <summary>The Jobs list's keys: <c>jobsQueryKeys</c> in <c>pages/jobs/jobsModel.ts</c>.</summary>
    public static IReadOnlyList<string> JobsQueryKeys { get; } = ["page", "type"];

    /// <summary>The jobs-by-type page's keys: <c>jobsByTypeQueryKeys</c> in <c>pages/jobs/jobsModel.ts</c>.</summary>
    public static IReadOnlyList<string> JobsByTypeQueryKeys { get; } = ["page", "metrics"];

    /// <summary>The states the Jobs list routes to (<c>/jobs/{state}</c>, one per job list the dashboard API serves).</summary>
    public static IReadOnlyList<string> JobStates { get; } =
        ["enqueued", "processing", "scheduled", "retrying", "awaiting", "completed", "failed", "deleted"];

    /// <summary>The query keys <paramref name="page"/> reads, or null for a page outside <see cref="Pages"/>.</summary>
    public static IReadOnlyList<string>? QueryKeys(string? page) => page switch
    {
        MetricPage => MetricQueryKeys,
        IssuesPage => IssuesQueryKeys,
        JobsPage => JobsQueryKeys,
        JobsByTypePage => JobsByTypeQueryKeys,
        _ => null,
    };

    /// <summary>Whether <paramref name="page"/> is a page a view can be saved on (exact, lower case).</summary>
    public static bool IsPage(string? page) => QueryKeys(page) is not null;

    /// <summary>
    /// Validates and normalizes <paramref name="view"/> in place (name, page and target trimmed, a blank target cleared, a
    /// leading <c>?</c> dropped from the query); on failure the error is a caller-facing message. The author, the shared
    /// flag and the timestamps are not checked: the command service takes the author from the principal.
    /// </summary>
    public static bool TryValidate(SavedView view, out string? error)
    {
        ArgumentNullException.ThrowIfNull(view);

        view.Name = (view.Name ?? string.Empty).Trim();
        view.Page = (view.Page ?? string.Empty).Trim();
        view.Target = string.IsNullOrWhiteSpace(view.Target) ? null : view.Target.Trim();
        view.Query = (view.Query ?? string.Empty).Trim();
        if (view.Query.StartsWith('?'))
        {
            view.Query = view.Query[1..];
        }

        if (view.Name.Length == 0 || view.Name.Length > MaxNameLength)
        {
            error = $"Name is required and must be at most {MaxNameLength} characters.";

            return false;
        }

        var keys = QueryKeys(view.Page);
        if (keys is null)
        {
            error = $"Page must be one of: {string.Join(", ", Pages)}.";

            return false;
        }

        if (!TryValidateTarget(view.Page, view.Target, out error))
        {
            return false;
        }

        if (view.Query.Length > MaxQueryLength)
        {
            error = $"Query must be at most {MaxQueryLength} characters.";

            return false;
        }

        foreach (var key in ParseKeys(view.Query))
        {
            if (key.Length == 0)
            {
                error = "Query holds a parameter without a name.";

                return false;
            }

            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                error = $"Query parameter '{Bounded(key)}' is not read by the {view.Page} page. Known parameters: {string.Join(", ", keys)}.";

                return false;
            }
        }

        error = null;

        return true;
    }

    /// <summary>The decoded names of the parameters in <paramref name="query"/>, a URL query string without <c>?</c>.</summary>
    public static IEnumerable<string> ParseKeys(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query
            .Split('&')
            .Where(x => x.Length > 0)
            .Select(x =>
            {
                var separator = x.IndexOf('=', StringComparison.Ordinal);
                var name = separator < 0 ? x : x[..separator];

                return Uri.UnescapeDataString(name.Replace('+', ' '));
            });
    }

    private static bool TryValidateTarget(string page, string? target, out string? error)
    {
        if (string.Equals(page, IssuesPage, StringComparison.Ordinal))
        {
            error = target is null ? null : "An issues view has no target.";

            return target is null;
        }

        if (target is null)
        {
            error = page switch
            {
                MetricPage => "A metric view needs a target: the instrument name.",
                JobsPage => "A jobs view needs a target: the job state.",
                _ => "A jobs-by-type view needs a target: the job type.",
            };

            return false;
        }

        if (target.Length > MaxTargetLength)
        {
            error = $"Target must be at most {MaxTargetLength} characters.";

            return false;
        }

        if (string.Equals(page, JobsPage, StringComparison.Ordinal) && !JobStates.Contains(target, StringComparer.Ordinal))
        {
            error = $"Target must be a job state: {string.Join(", ", JobStates)}.";

            return false;
        }

        error = null;

        return true;
    }

    // An echoed key is caller input: bounded so the message stays short.
    private static string Bounded(string key) => key.Length > 64 ? key[..64] + "..." : key;
}
