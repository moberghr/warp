using System.Data.Common;

namespace Warp.Core.Services;

/// <summary>
/// Builds the connection description shown in the dashboard footer. A connection string legally carries
/// credentials, so this reads only the host and database name out of it and never echoes it wholesale.
/// </summary>
internal static class DatabaseConnectionDescriptor
{
    // Every alias the two shipped providers accept for the server and the database.
    private static readonly string[] HostKeys = ["Host", "Server", "Data Source", "Address", "Addr", "Network Address"];
    private static readonly string[] DatabaseKeys = ["Database", "Initial Catalog"];

    public static string? Describe(string? providerName, string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        var provider = ProviderLabel(providerName);

        // DbConnectionStringBuilder is the ADO.NET grammar itself: it honours quoting, so a password
        // containing ';' or '=' cannot leak into the host, and a repeated key resolves last-wins the
        // same way SqlConnectionStringBuilder does. The hand-rolled split this replaces got the
        // second right and the first wrong.
        DbConnectionStringBuilder parsed;
        try
        {
            parsed = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            // Not a well-formed connection string. The provider would have refused it too, so this is
            // the footer for a context that cannot connect — say what is known and no more.
            return provider;
        }

        var host = First(parsed, HostKeys) ?? "unknown";
        var db = First(parsed, DatabaseKeys) ?? string.Empty;

        return $"{provider}: Host: {host}, DB: {db}";
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

    private static string? First(DbConnectionStringBuilder parsed, string[] keys)
    {
        foreach (var key in keys)
        {
            if (parsed.TryGetValue(key, out var value) && value is string text && text.Length > 0)
            {
                return text;
            }
        }

        return null;
    }
}
