namespace Warp.Core.Adapters;

/// <summary>
/// Validation for adapter names. The name is the adapter's cluster-wide identity and is embedded in
/// colon-delimited <c>Counter</c> keys (<c>adapter:{name}:...</c>) and the 200-char <c>AdapterName</c>
/// column. A <c>':'</c> in the name would break counter-key parsing (the dashboard silently drops
/// unparseable keys), and an over-long name would fail the call-log insert — so both are rejected up
/// front at registration (<c>AddAdapter</c>) and at call time (<c>BeginCall</c>).
/// </summary>
public static class AdapterName
{
    /// <summary>Maximum adapter-name length — matches the <c>AdapterCallLog.AdapterName</c> column cap.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="name"/> is null/blank, longer than
    /// <see cref="MaxLength"/>, or contains a <c>':'</c> (the counter-key delimiter). <paramref name="paramName"/>
    /// names the offending argument in the thrown exception (e.g. <c>"adapter"</c> at <c>BeginCall</c>).
    /// <para>
    /// <b>Adapter names are case-SENSITIVE.</b> Identity is compared ordinally everywhere — the in-memory
    /// registry/state dictionaries, the DB <c>AdapterDefinition</c>/<c>AdapterCallLog</c> rows, and the
    /// <c>adapter:{name}:...</c> counter keys. "Stripe" and "stripe" are two distinct adapters with
    /// independent stats and rate-limit budgets; pick one canonical casing per adapter.
    /// </para>
    /// </summary>
    public static void Validate(string name, string paramName = "name")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Adapter name must be a non-empty, non-whitespace string.", paramName);
        }

        if (name.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Adapter name '{name}' exceeds the {MaxLength}-character limit.",
                paramName);
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Adapter name '{name}' must not contain ':' — it delimits counter keys and would drop the adapter from dashboard stats.",
                paramName);
        }
    }
}
