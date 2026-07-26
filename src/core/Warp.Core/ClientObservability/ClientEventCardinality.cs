using System.Collections.Concurrent;
using Warp.Core.Enums;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Bounds the per-name aggregate dimension so browser-controlled names can't explode the <c>clientevent:</c>
/// Counter key space or the <c>warp.client.vitals</c> meter-tag cardinality (§8.19 cardinality guard — the
/// difference from endpoints, whose route identities are already bounded). This is a PUBLIC endpoint, so
/// NOTHING a client sends is trusted to be bounded:
/// <list type="bullet">
/// <item>Error / Event / Log names collapse to <see cref="Other"/> once the first N distinct names per type
/// are seen (log's dimension is its level).</item>
/// <item>Vital names are matched against the fixed <see cref="KnownVitals"/> allowlist (the 5 Core Web Vitals);
/// anything else collapses to <see cref="Other"/> — an allowlist, not a cap, because the set is truly fixed.</item>
/// </list>
/// Only the aggregate key/tag is bounded — the raw <see cref="Data.Entities.ClientEventLog"/> row keeps the
/// real name.
/// </summary>
public sealed class ClientEventCardinality
{
    public const string Other = "{other}";

    /// <summary>The 5 Core Web Vitals — the only vital names that keep their own aggregate key/meter tag.</summary>
    public static readonly IReadOnlySet<string> KnownVitals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LCP", "CLS", "INP", "FCP", "TTFB",
    };

    private readonly int _maxErrorNames;
    private readonly int _maxEventNames;
    private readonly int _maxLogNames;
    private readonly ConcurrentDictionary<string, byte> _errorNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _eventNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _logNames = new(StringComparer.Ordinal);

    public ClientEventCardinality(int maxErrorNames, int maxEventNames, int maxLogNames)
    {
        _maxErrorNames = maxErrorNames <= 0 ? 1 : maxErrorNames;
        _maxEventNames = maxEventNames <= 0 ? 1 : maxEventNames;
        _maxLogNames = maxLogNames <= 0 ? 1 : maxLogNames;
    }

    /// <summary>Returns the canonical vital name (upper-cased) when it's a known Core Web Vital, else <see cref="Other"/>. Null ⇒ null.</summary>
    public static string? NormalizeVital(string? name)
    {
        if (name is null)
        {
            return null;
        }

        return KnownVitals.Contains(name) ? name.ToUpperInvariant() : Other;
    }

    /// <summary>
    /// Returns the name to use in the aggregate key: the real name while under the per-type cap (or an
    /// allowlisted vital), else <see cref="Other"/>. Null in ⇒ null out (no per-name key).
    /// </summary>
    public string? Resolve(ClientEventType type, string? name)
    {
        if (name is null)
        {
            return null;
        }

        return type switch
        {
            ClientEventType.Error => Admit(_errorNames, name, _maxErrorNames),
            ClientEventType.Event => Admit(_eventNames, name, _maxEventNames),
            ClientEventType.Log => Admit(_logNames, name, _maxLogNames),
            ClientEventType.Vital => NormalizeVital(name),
            _ => Other,
        };
    }

    private static string Admit(ConcurrentDictionary<string, byte> seen, string name, int cap)
    {
        if (seen.ContainsKey(name))
        {
            return name;
        }

        // Approximate cap: a benign race can admit a few over the cap, never unbounded. Once full, unseen
        // names collapse to {other} — which itself occupies one slot so it always folds cleanly.
        if (seen.Count < cap)
        {
            seen.TryAdd(name, 0);

            return name;
        }

        return Other;
    }
}
