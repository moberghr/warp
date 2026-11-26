using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data;

namespace Warp.Provider.SqlServer;

// The batched completion and keep-alive read JSON with OPENJSON (rules 6.13, 6.14), which needs database
// compatibility level 130. A database left lower after a server upgrade lets the claim work and fails the
// completion after the handler ran: jobs run and are never finalized. So a server refuses to start.
internal static class SqlServerCompatibilityCheck
{
    public const int RequiredLevel = 130;

    public static async Task EnsureAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT DB_NAME(), compatibility_level FROM sys.databases WHERE database_id = DB_ID();", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return;
        }

        var database = reader.GetString(0);
        var level = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        if (level < RequiredLevel)
        {
            throw new InvalidOperationException(
                $"Warp needs SQL Server database compatibility level {RequiredLevel} or higher (OPENJSON); database '{database}' is at {level}. " +
                $"Run: ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = {RequiredLevel};");
        }
    }
}

// Runs the check when a Warp server starts. A process that only publishes (AddWarp) runs none of the
// statements that need the level, so it is left alone. A context on another provider (EF in-memory in an
// application's own tests) has no database to ask, so the check stands down. With RequireDatabaseAtStartup off,
// an unreachable server defers the check through the startup gate, and the server registration waits for it.
internal sealed class SqlServerCompatibilityCheckService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly WarpDatabaseConnection<TContext> _connection;
    private readonly IEnumerable<IWarpServerPresence> _servers;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SqlServerCompatibilityCheckService<TContext>> _logger;
    private readonly WarpStartupGate _gate;

    public SqlServerCompatibilityCheckService(
        WarpDatabaseConnection<TContext> connection,
        IEnumerable<IWarpServerPresence> servers,
        IServiceScopeFactory scopes,
        ILogger<SqlServerCompatibilityCheckService<TContext>> logger,
        WarpStartupGate gate)
    {
        _connection = connection;
        _servers = servers;
        _scopes = scopes;
        _logger = logger;
        _gate = gate;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_servers.Any())
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TContext>().Database;
        if (!database.IsSqlServer())
        {
            _logger.LogDebug(
                "Skipping Warp's SQL Server compatibility check for {Context}: its provider is {Provider}, not SQL Server.",
                typeof(TContext).Name,
                database.ProviderName);

            return;
        }

        await _gate.RunCheckAsync(
            "SQL Server compatibility-level check",
            x => SqlServerCompatibilityCheck.EnsureAsync(_connection.ConnectionString, x),
            _logger,
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _gate.StopAsync();
}
