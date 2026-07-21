using System.Globalization;
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

    // Dimension marker for the latency histogram buckets. A pct key is Total-only and has the fixed shape
    // endpoint:{route}:pct:{upperMs} — parts.Length == 4 with this marker — so TryParse (which only knows
    // Total at length 3 and grp at length >= 5) never folds it into the count/error StatSet.
    public const string PctMarker = "pct";

    // Marker for the hourly time-series buckets. An hourly key has the fixed shape
    // endpoint:{route}:hist:{outcome}:{yyyy-MM-dd-HH} — its trailing segment is a date, so the generic
    // hourly-stat sweep in ExpirationCleanup prunes it at 7 days with no bespoke cleanup, and TryParse
    // (which matches only grp at this length) rejects it so it never pollutes the lifetime count/error/pct
    // StatSet. Read separately via TryParseHistory to build the per-endpoint performance chart.
    public const string HistoryMarker = "hist";

    // Ascending latency-bucket upper bounds (ms); the trailing int.MaxValue is the "> 10000 ms" catch-all
    // overflow bucket. A single call increments the ONE bucket whose bound is the smallest >= its rounded
    // ms (see BucketFor); the read side walks these cumulatively to derive p90/p95/p99.
    public static readonly int[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, int.MaxValue];

    public static string Total(string route, string outcome) => $"{Prefix}:{route}:{outcome}";

    public static string Group(string route, string group, string outcome) => $"{Prefix}:{route}:grp:{group}:{outcome}";

    public static string Pct(string route, int upperMs) => $"{Prefix}:{route}:{PctMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}";

    public static string History(string route, string outcome, string hour) => $"{Prefix}:{route}:{HistoryMarker}:{outcome}:{hour}";

    // The hourly bucket label (UTC) a timestamp falls in — the trailing segment of a history key. Matches
    // the "yyyy-MM-dd-HH" format the job-stats history and the generic hourly-stat cleanup both use.
    public static string HourBucket(DateTime timestampUtc) => timestampUtc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);

    // The smallest bucket upper bound that is >= the rounded duration. Buckets is ascending and its last
    // entry is int.MaxValue, so First always matches (the final entry is the "> 10000 ms" catch-all).
    public static int BucketFor(int durationMs) => Buckets.First(bound => durationMs <= bound);

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
    // constraint-free) and the detail page can join log rows to aggregate stats. Any literal ':' left after
    // constraint-stripping (e.g. the custom-method syntax "orders/{id}:export") is replaced with '-' so the
    // route is GUARANTEED colon-free — otherwise it would corrupt the colon-delimited counter key and the
    // endpoint would silently vanish from the aggregate stats.
    public static string NormalizeTemplate(string routeTemplate)
        => ConstraintRegex().Replace(routeTemplate, "{${name}}").Replace(':', '-');

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

        // Latency histogram buckets (endpoint:{route}:pct:{upperMs}) are NOT count/error rows — they are
        // read separately via TryParsePct. Reject them here so they never pollute the count/error StatSet.
        if (string.Equals(marker, PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var group = string.Join(':', parts[3..^1]);
        var outcome = parts[^1];

        if (parts.Length >= 5 && string.Equals(marker, "grp", StringComparison.Ordinal))
        {
            parsed = new EndpointCounterKey(route, EndpointStatDimension.Group, group, outcome);

            return true;
        }

        return false;
    }

    // Parses a latency-histogram bucket key (endpoint:{route}:pct:{upperMs}). Returns false for every
    // other key shape — the disjoint counterpart to TryParse, which rejects pct keys.
    public static bool TryParsePct(string key, out string route, out int upperMs)
    {
        route = string.Empty;
        upperMs = 0;

        var parts = key.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[2], PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        route = parts[1];

        return true;
    }

    // Parses an hourly time-series bucket key (endpoint:{route}:hist:{outcome}:{yyyy-MM-dd-HH}). Returns
    // false for every other key shape — the disjoint counterpart to TryParse, which rejects hist keys. The
    // outcome is the count outcome token (success/failed) or the DurationToken; the read side sums them per
    // hour into calls / errors / duration for the performance chart.
    public static bool TryParseHistory(string key, out string route, out string outcome, out DateTime hour)
    {
        route = string.Empty;
        outcome = string.Empty;
        hour = default;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[2], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParseExact(parts[4], "yyyy-MM-dd-HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour))
        {
            return false;
        }

        route = parts[1];
        outcome = parts[3];

        return true;
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
