using System.Globalization;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Builds and parses the free-form <see cref="Counter"/> keys for client (browser) event statistics (§8.27) —
/// the durable trend layer that <b>survives raw <see cref="ClientEventLog"/> cleanup</b> via the standard
/// <c>Counter → CounterAggregator → Statistic</c> fold (§8.22). Keys live under their OWN top-level prefixes
/// (<see cref="Prefix"/> <c>"clientevent"</c> / <see cref="AppPrefix"/> <c>"clientevent-app"</c>), DISJOINT from
/// every existing family (first-segment-equality parsers reject them, §8.6/§8.19).
/// <para>
/// Dimensions (fixed marker at segment 1 keeps parsing unambiguous regardless of type/name values):
/// per-type total + hourly history (<c>total</c>), per-name lifetime total (<c>name</c> — error type / event
/// name / log level, collapsed to a bounded set by <c>ClientEventCardinality</c> BEFORE this builder so
/// browser-controlled names can't explode the key space, §8.19), and per-vital count + duration-sum +
/// latency histogram (<c>vital</c>) so vital avg + <b>p75</b> (Google's Core-Web-Vitals percentile) survive
/// cleanup. Per-app carries the per-type total only, to bound volume (mirrors the queue-wait per-app slice).
/// </para>
/// </summary>
internal static class ClientEventKeys
{
    public const string Prefix = "clientevent";

    public const string AppPrefix = "clientevent-app";

    public const string TotalMarker = "total";

    public const string NameMarker = "name";

    public const string VitalMarker = "vital";

    public const string HistoryMarker = "hist";

    public const string PctMarker = "pct";

    public const string CountToken = "count";

    public const string DurationToken = "dur";

    // Vital-oriented buckets (ms): fine at the low end so CLS (scaled ×1000 → 0..~500) and INP (0..~500) get
    // real resolution, coarse up top for LCP/FCP/TTFB. p75 is read off this histogram.
    public static readonly int[] Buckets = [50, 100, 200, 300, 500, 800, 1000, 1500, 2000, 2500, 3000, 4000, 5000, 7500, 10000, int.MaxValue];

    public static string TypeToken(ClientEventType type) => type switch
    {
        ClientEventType.Error => "error",
        ClientEventType.Vital => "vital",
        ClientEventType.Log => "log",
        ClientEventType.Event => "event",
        _ => "unknown",
    };

    public static string TypeTotal(string typeToken) => $"{Prefix}:{TotalMarker}:{typeToken}:{CountToken}";

    public static string TypeHistory(string typeToken, string hour) => $"{Prefix}:{TotalMarker}:{typeToken}:{HistoryMarker}:{hour}";

    public static string NameTotal(string typeToken, string name) => $"{Prefix}:{NameMarker}:{typeToken}:{name}:{CountToken}";

    public static string Vital(string name, string token) => $"{Prefix}:{VitalMarker}:{name}:{token}";

    public static string VitalPct(string name, int upperMs) => $"{Prefix}:{VitalMarker}:{name}:{PctMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}";

    public static string AppTypeTotal(string application, string typeToken) => $"{AppPrefix}:{application}:{TotalMarker}:{typeToken}:{CountToken}";

    public static string HourBucket(DateTime timestampUtc) => timestampUtc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);

    public static int BucketFor(int valueMs) => Buckets.First(bound => valueMs <= bound);

    public static string Sanitize(string value) => value.Replace(':', '-');

    // CLS is a unitless 0..~1 score; scale it into the shared integer ms bucket set so one histogram works for
    // every vital. All other vitals are already in milliseconds.
    public static double NormalizeVitalValue(string name, double value)
    {
        if (string.Equals(name, "CLS", StringComparison.OrdinalIgnoreCase))
        {
            return value * 1000;
        }

        return value;
    }

    /// <summary>
    /// Produces every client-event counter for one ingested event. Pure construction — no reads, no state — so
    /// the caller (the flusher) can batch the rows into one <c>SaveChanges</c>. <paramref name="name"/> is the
    /// per-name dimension (error type / event name / log level / vital name) and must already be collapsed to a
    /// bounded set by <c>ClientEventCardinality</c>; <paramref name="value"/> is the vital measurement
    /// (raw — CLS is scaled here).
    /// </summary>
    public static List<Counter> Build(ClientEventType type, string? name, double? value, string? application, string hourBucket)
    {
        var counters = new List<Counter>();
        var typeToken = TypeToken(type);

        counters.Add(new Counter { Key = TypeTotal(typeToken), Value = 1 });
        counters.Add(new Counter { Key = TypeHistory(typeToken, hourBucket), Value = 1 });

        if (application is not null)
        {
            counters.Add(new Counter { Key = AppTypeTotal(Sanitize(application), typeToken), Value = 1 });
        }

        if (type == ClientEventType.Vital)
        {
            if (name is not null && value.HasValue)
            {
                var vital = Sanitize(name);
                var scaled = (int)Math.Min(int.MaxValue, Math.Round(Math.Max(0, NormalizeVitalValue(name, value.Value)), MidpointRounding.AwayFromZero));
                counters.Add(new Counter { Key = Vital(vital, CountToken), Value = 1 });
                counters.Add(new Counter { Key = Vital(vital, DurationToken), Value = scaled });
                counters.Add(new Counter { Key = VitalPct(vital, BucketFor(scaled)), Value = 1 });
            }

            return counters;
        }

        if (name is not null)
        {
            counters.Add(new Counter { Key = NameTotal(typeToken, Sanitize(name)), Value = 1 });
        }

        return counters;
    }

    // Parses a per-type total key (clientevent:total:{type}:count).
    public static bool TryParseTypeTotal(string key, out string typeToken)
    {
        typeToken = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[1], TotalMarker, StringComparison.Ordinal) || !string.Equals(parts[3], CountToken, StringComparison.Ordinal))
        {
            return false;
        }

        typeToken = parts[2];

        return true;
    }

    // Parses a per-type hourly history key (clientevent:total:{type}:hist:{hour}).
    public static bool TryParseTypeHistory(string key, out string typeToken, out string hour)
    {
        typeToken = string.Empty;
        hour = string.Empty;

        var parts = key.Split(':');
        if (parts.Length is not (5 or 6) || !string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[1], TotalMarker, StringComparison.Ordinal) || !string.Equals(parts[3], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (parts.Length == 6)
        {
            // Tiered key rolled by StatisticRollup (clientevent:total:{type}:hist:{tier}:{stamp}, §8.30) —
            // down-bin to the hour string the read side already parses (a daily bucket reports its midnight).
            if (!Warp.Core.Services.MetricTiers.TryParse(parts[4], parts[5], out _, out var bucket))
            {
                return false;
            }

            hour = bucket.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            hour = parts[4]; // legacy unmarked yyyy-MM-dd-HH
        }

        typeToken = parts[2];

        return true;
    }

    // Parses a per-name lifetime total key (clientevent:name:{type}:{name}:count).
    public static bool TryParseNameTotal(string key, out string typeToken, out string name)
    {
        typeToken = string.Empty;
        name = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[1], NameMarker, StringComparison.Ordinal) || !string.Equals(parts[4], CountToken, StringComparison.Ordinal))
        {
            return false;
        }

        typeToken = parts[2];
        name = parts[3];

        return true;
    }

    // Parses a vital count/duration key (clientevent:vital:{name}:{count|dur}).
    public static bool TryParseVital(string key, out string name, out string token)
    {
        name = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[1], VitalMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[3], CountToken, StringComparison.Ordinal) && !string.Equals(parts[3], DurationToken, StringComparison.Ordinal))
        {
            return false;
        }

        name = parts[2];
        token = parts[3];

        return true;
    }

    // Parses a vital latency-histogram bucket key (clientevent:vital:{name}:pct:{upperMs}).
    public static bool TryParseVitalPct(string key, out string name, out int upperMs)
    {
        name = string.Empty;
        upperMs = 0;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[1], VitalMarker, StringComparison.Ordinal) || !string.Equals(parts[3], PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        name = parts[2];

        return true;
    }

    // Parses a per-app per-type total key (clientevent-app:{app}:total:{type}:count).
    public static bool TryParseAppTypeTotal(string key, out string application, out string typeToken)
    {
        application = string.Empty;
        typeToken = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], AppPrefix, StringComparison.Ordinal) || !string.Equals(parts[2], TotalMarker, StringComparison.Ordinal) || !string.Equals(parts[4], CountToken, StringComparison.Ordinal))
        {
            return false;
        }

        application = parts[1];
        typeToken = parts[3];

        return true;
    }
}
