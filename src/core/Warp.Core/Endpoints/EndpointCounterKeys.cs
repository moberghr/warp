using System.Text.RegularExpressions;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Endpoints;

/// <summary>
/// Builds the free-form <see cref="Counter"/> keys for inbound endpoint statistics — the inbound mirror
/// of <c>AdapterCounterKeys</c>, but simpler: the route IS the operation, so there is no per-operation
/// dimension, only <see cref="EndpointStatDimension.Total"/> and <see cref="EndpointStatDimension.Group"/>.
/// Keys are colon-delimited and namespaced under <c>endpoint:</c> so <c>CounterAggregator</c> (which
/// groups by exact key) folds each dimension into its own <c>Statistic</c> row, queryable per route /
/// group / outcome. The route is normalised colon-free (see <see cref="NormalizeRoute"/>) so the
/// colon-delimited key parses unambiguously; the outcome token is always the trailing segment and is
/// never a date, so hourly-bucket cleanup / history parsing never mistakes an endpoint key for an
/// hourly stat.
/// </summary>
internal static partial class EndpointCounterKeys
{
    public const string Prefix = "endpoint";

    // Reserved trailing token for the per-dimension duration SUM (ms). Rides the same key layout + Counter→
    // Statistic aggregation as the per-outcome COUNT tokens, so average latency (sum ÷ count) survives
    // EndpointCallLog deletion. Never an AdapterCallOutcome token, so parsing folds it into DurationSum,
    // not the call Total.
    public const string DurationToken = "dur";

    public static string Total(string route, string outcome) => $"{Prefix}:{route}:{outcome}";

    public static string Group(string route, string group, string outcome) => $"{Prefix}:{route}:grp:{group}:{outcome}";

    public static string OutcomeToken(AdapterCallOutcome outcome) => outcome switch
    {
        AdapterCallOutcome.Success => "success",
        AdapterCallOutcome.Failed => "failed",
        _ => "unknown",
    };

    // Produces the stable "{METHOD} {template}" route identity used as the key's route segment. Inline
    // route constraints ({name:int}, {name:int=5}, {*name:...}) are stripped so the route is colon-free
    // and stable across constraint changes — this GUARANTEES the route contains no ':' so the
    // colon-delimited key parses unambiguously (route is always a single part). Route parameter names
    // contain no braces, so one Replace pass fully normalises.
    public static string NormalizeRoute(string method, string routeTemplate)
        => $"{method.ToUpperInvariant()} {NormalizeTemplate(routeTemplate)}";

    // Strips inline route constraints from a template only (no method). The flusher stamps this onto the
    // EndpointCallLog row so the stored identity matches the counter-key route exactly (both colon-free,
    // constraint-free) and the detail page can join log rows to aggregate stats.
    public static string NormalizeTemplate(string routeTemplate) => ConstraintRegex().Replace(routeTemplate, "{${name}}");

    // Inverse of the builders above — kept in the SAME type so the key format and its parser can never
    // drift apart (drift silently zeroes the dashboard, which drops unparseable keys). Layout:
    //   endpoint:{route}:{outcome}                → total
    //   endpoint:{route}:grp:{group}:{outcome}    → per-group
    // The route is guaranteed colon-free (see NormalizeRoute), so parts[1] is always the whole route.
    // The group value may itself contain ':', so it is everything between the "grp" marker and the
    // trailing outcome token.
    public static bool TryParse(string key, out EndpointCounterKey parsed)
    {
        parsed = default;

        var parts = key.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var route = parts[1];

        if (parts.Length == 3)
        {
            parsed = new EndpointCounterKey(route, EndpointStatDimension.Total, string.Empty, parts[^1]);

            return true;
        }

        var marker = parts[2];
        var group = string.Join(':', parts[3..^1]);
        var outcome = parts[^1];

        if (parts.Length >= 5 && string.Equals(marker, "grp", StringComparison.Ordinal))
        {
            parsed = new EndpointCounterKey(route, EndpointStatDimension.Group, group, outcome);

            return true;
        }

        return false;
    }

    [GeneratedRegex(@"\{(?<name>[^{}:]+):[^{}]*\}", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ConstraintRegex();
}

/// <summary>The stat dimension a parsed endpoint <see cref="Counter"/> key belongs to.</summary>
internal enum EndpointStatDimension
{
    Total = 1,
    Group = 2,
}

/// <summary>The parsed components of an endpoint <see cref="Counter"/> / <see cref="Statistic"/> key.</summary>
internal readonly record struct EndpointCounterKey(string Route, EndpointStatDimension Dimension, string Group, string Outcome);
