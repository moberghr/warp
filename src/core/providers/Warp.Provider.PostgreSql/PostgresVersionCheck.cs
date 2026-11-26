using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core;

namespace Warp.Provider.PostgreSql;

// The claim stamps each run with gen_random_uuid() (rule 6.13), which is built in from PostgreSQL 13. On an
// older server without pgcrypto the server starts and every claim fails, so no job runs and nothing says why
// but a statement error per poll. So a server refuses to start.
internal static class PostgresVersionCheck
{
    public const int RequiredMajor = 13;

    /// <summary>Throws when <paramref name="versionNumber"/> (PostgreSQL's <c>server_version_num</c>, 130004 for 13.4) is older than <see cref="RequiredMajor"/>.</summary>
    public static void Ensure(int versionNumber)
    {
        var major = versionNumber / 10000;
        if (major >= RequiredMajor)
        {
            return;
        }

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Warp needs PostgreSQL {RequiredMajor} or newer (gen_random_uuid); this server reports version number {versionNumber} (major {major}). ") +
            "See the 8.0 upgrade guide, section PostgreSQL.");
    }

    public static async Task EnsureAsync(DbContext context, CancellationToken ct)
    {
        var versionNumber = await context.Database
            .SqlQueryRaw<int>("SELECT current_setting('server_version_num')::int AS \"Value\"")
            .SingleAsync(ct);

        Ensure(versionNumber);
    }
}

// Runs the check when a Warp server starts. A process that only publishes (AddWarp) claims nothing, so it is
// left alone. The query goes through the application's context so a host's own data source is honoured.
// A context on another provider (EF in-memory in an application's own tests) has no server to ask.
internal sealed class PostgresVersionCheckService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IEnumerable<IWarpServerPresence> _servers;
    private readonly ILogger<PostgresVersionCheckService<TContext>> _logger;

    public PostgresVersionCheckService(IServiceScopeFactory scopes, IEnumerable<IWarpServerPresence> servers, ILogger<PostgresVersionCheckService<TContext>> logger)
    {
        _scopes = scopes;
        _servers = servers;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_servers.Any())
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        if (!context.Database.IsNpgsql())
        {
            _logger.LogDebug(
                "Skipping Warp's PostgreSQL version check for {Context}: its provider is {Provider}, not Npgsql.",
                typeof(TContext).Name,
                context.Database.ProviderName);

            return;
        }

        await PostgresVersionCheck.EnsureAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
