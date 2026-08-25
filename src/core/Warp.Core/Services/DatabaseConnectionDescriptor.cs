namespace Warp.Core.Services;

/// <summary>
/// Builds the connection description shown in the dashboard footer. A connection string legally carries
/// credentials, so this reads only the host and database name out of it and never echoes it wholesale.
/// </summary>
internal static class DatabaseConnectionDescriptor
{
    public static string? Describe(string? providerName, string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        var parts = ParseKeys(connectionString);
        var host = parts.GetValueOrDefault("Host")
            ?? parts.GetValueOrDefault("Server")
            ?? parts.GetValueOrDefault("Data Source")
            ?? "unknown";
        var db = parts.GetValueOrDefault("Database") ?? parts.GetValueOrDefault("Initial Catalog") ?? string.Empty;

        return $"{ProviderLabel(providerName)}: Host: {host}, DB: {db}";
    }

    // The provider comes from EF, not from which keys the connection string happens to use. Npgsql accepts
    // `Server=` as an alias for `Host=`, so keying off the presence of a `Host` key labelled a perfectly
    // ordinary Postgres connection string "SQL Server" — and the footer states the provider prominently.
    private static string ProviderLabel(string? providerName)
    {
        if (providerName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "PostgreSQL Server";
        }

        if (providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "SQL Server";
        }

        // Warp ships two providers; anything else is a host's own choice, and naming a provider we cannot
        // identify is how the original bug read. Say what we know instead of guessing.
        return "Database";
    }

    private static Dictionary<string, string> ParseKeys(string connectionString)
    {
        // A connection string can legally contain the same key twice — ADO.NET's
        // SqlConnectionStringBuilder resolves this by taking the LAST value. Tests
        // that scope per-server connection pools by appending `Application Name=...`
        // to an already-configured base string produce this shape, and a naive
        // ToDictionary throws on the duplicate. Fold via last-wins.
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            parts[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        return parts;
    }
}
