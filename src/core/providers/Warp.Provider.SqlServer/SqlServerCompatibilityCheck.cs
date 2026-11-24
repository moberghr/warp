using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
// statements that need the level, so it is left alone.
internal sealed class SqlServerCompatibilityCheckService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly WarpDatabaseConnection<TContext> _connection;
    private readonly IEnumerable<IWarpServerPresence> _servers;

    public SqlServerCompatibilityCheckService(WarpDatabaseConnection<TContext> connection, IEnumerable<IWarpServerPresence> servers)
    {
        _connection = connection;
        _servers = servers;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _servers.Any()
            ? SqlServerCompatibilityCheck.EnsureAsync(_connection.ConnectionString, cancellationToken)
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
