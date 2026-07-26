using System.Collections.Concurrent;
using Warp.Core.Enums;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Bounds the per-name aggregate dimension so browser-controlled names (error types, custom event names) can't
/// explode the <c>clientevent:</c> Counter key space (§8.19 cardinality guard — the difference from endpoints,
/// whose route identities are already bounded). The FIRST N distinct names seen per <see cref="ClientEventType"/>
/// keep their own key; every later name folds into a literal <see cref="Other"/> bucket. Vital names (5 Core
/// Web Vitals) and log levels are inherently bounded, so they are never collapsed. Only the aggregate key is
/// bounded — the raw <see cref="Data.Entities.ClientEventLog"/> row always keeps the real name.
/// </summary>
public sealed class ClientEventCardinality
{
    public const string Other = "{other}";

    private readonly int _maxErrorNames;
    private readonly int _maxEventNames;
    private readonly ConcurrentDictionary<string, byte> _errorNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _eventNames = new(StringComparer.Ordinal);

    public ClientEventCardinality(int maxErrorNames, int maxEventNames)
    {
        _maxErrorNames = maxErrorNames <= 0 ? 1 : maxErrorNames;
        _maxEventNames = maxEventNames <= 0 ? 1 : maxEventNames;
    }

    /// <summary>
    /// Returns the name to use in the aggregate key: the real name while under the per-type cap, else
    /// <see cref="Other"/>. Null in ⇒ null out (no per-name key). Vital/Log names pass through unbounded.
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
            _ => name,
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
