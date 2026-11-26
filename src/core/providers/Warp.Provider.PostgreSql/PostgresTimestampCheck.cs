using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Entities;

namespace Warp.Provider.PostgreSql;

// Warp's time columns must be timestamp with time zone (upgrading-to-8.md, "Legacy timestamp mode"): 8.0
// sends its timestamps typed as timestamptz and compares them with the database's now(). Under Npgsql's
// legacy timestamp mode (the Npgsql.EnableLegacyTimestampBehavior AppContext switch) EF maps DateTime to
// timestamp without time zone, and every one of those comparisons then depends on the session time zone:
// leases, keep-alives and schedules read hours off on a server whose time zone is not UTC. Nothing fails
// loudly, so a process refuses to start instead. The model is what is checked, not the switch: it is what
// the migrations were built from, and it also catches a column a host retyped by hand.
internal static class PostgresTimestampCheck
{
    public const string RequiredStoreType = "timestamp with time zone";

    /// <summary>Warp's DateTime properties whose column is not <see cref="RequiredStoreType"/>, as "Entity.Property (store type)".</summary>
    public static List<string> FindLegacyColumns(IModel model)
    {
        var warpAssembly = typeof(Job).Assembly;

        return
        [
            .. model.GetEntityTypes()
            .Where(x => x.ClrType.Assembly == warpAssembly)
            .SelectMany(x => x.GetProperties().Select(y => (Entity: x, Property: y)))
            .Where(x => (Nullable.GetUnderlyingType(x.Property.ClrType) ?? x.Property.ClrType) == typeof(DateTime))
            .Select(x => (x.Entity, x.Property, StoreType: x.Property.GetColumnType()))
            .Where(x => !IsTimestampWithTimeZone(x.StoreType))
            .Select(x => $"{x.Entity.ClrType.Name}.{x.Property.Name} ({x.StoreType})")
            .OrderBy(x => x, StringComparer.Ordinal),
        ];
    }

    public static void Ensure(IModel model)
    {
        var legacy = FindLegacyColumns(model);
        if (legacy.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Warp needs its time columns to be '{RequiredStoreType}' on PostgreSQL, and {legacy.Count} are not: {string.Join(", ", legacy.Take(5))}{(legacy.Count > 5 ? ", ..." : string.Empty)}. " +
            "This is Npgsql's legacy timestamp mode (the 'Npgsql.EnableLegacyTimestampBehavior' AppContext switch), which Warp does not support: " +
            "remove the switch, add a migration that converts the columns, and apply it before starting Warp. " +
            "See the 8.0 upgrade guide, section PostgreSQL.");
    }

    private static bool IsTimestampWithTimeZone(string? storeType) =>
        string.Equals(storeType, RequiredStoreType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(storeType, "timestamptz", StringComparison.OrdinalIgnoreCase);
}

// Runs the check when the host starts, in every process that uses the provider: a publisher writes the
// schedule times a server compares, so it is held to the same columns. It reads the model only, no database.
// A context on another provider (EF in-memory in an application's own tests) has no column types to check
// (GetColumnType is relational-only), so the check stands down.
internal sealed class PostgresTimestampCheckService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PostgresTimestampCheckService<TContext>> _logger;

    public PostgresTimestampCheckService(IServiceScopeFactory scopes, ILogger<PostgresTimestampCheckService<TContext>> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        if (!context.Database.IsNpgsql())
        {
            _logger.LogDebug(
                "Skipping Warp's PostgreSQL timestamp check for {Context}: its provider is {Provider}, not Npgsql.",
                typeof(TContext).Name,
                context.Database.ProviderName);

            return Task.CompletedTask;
        }

        PostgresTimestampCheck.Ensure(context.Model);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
