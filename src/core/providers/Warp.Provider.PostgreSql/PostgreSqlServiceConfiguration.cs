using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Queries;
using Warp.Core.Notifications;
using Warp.Worker;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-specific provider services (row-lock SQL, exception classifier)
/// for Warp. Call <c>opt.UsePostgreSql()</c> inside the <c>AddWarp</c> or
/// <c>AddWarpServer</c> lambda to opt in.
/// </summary>
public static class PostgreSqlServiceConfiguration
{
    /// <summary>Suffix appended to <c>Application Name</c> for the advisory-lock pool — see <see cref="ResolveLockConnectionString{TContext}"/>.</summary>
    private const string LockApplicationName = "warp-locks";

    /// <summary>Postgres stores <c>application_name</c> in a <c>NAMEDATALEN-1</c> field and truncates past it silently.</summary>
    private const int MaxApplicationNameLength = 63;

    /// <summary>
    /// Registers the PostgreSQL provider services for Warp — row-lock SQL, exception classifier,
    /// notification transport, lock and semaphore providers, and the server-context configurator.
    /// </summary>
    /// <typeparam name="TContext">The host's DbContext.</typeparam>
    /// <param name="builder">The Warp builder being configured.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IWarpBuilder<TContext> UsePostgreSql<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext =>
        builder.UsePostgreSql(lockDataSource: null);

    /// <summary>
    /// Registers the PostgreSQL provider services for Warp, with a dedicated data source for
    /// advisory-lock sessions.
    /// </summary>
    /// <typeparam name="TContext">The host's DbContext.</typeparam>
    /// <param name="builder">The Warp builder being configured.</param>
    /// <param name="lockDataSource">
    /// Optional dedicated data source for advisory-lock sessions, so they get their own Npgsql pool
    /// (see <see cref="ResolveLockConnectionString{TContext}"/> for what that is worth and why).
    /// <para>
    /// Only needed when <typeparamref name="TContext"/> is registered with an
    /// <see cref="NpgsqlDataSource"/> — Aspire, Managed Identity, client certificates. On that path
    /// Warp cannot derive a second pool on its own: a data source's password provider, certificate
    /// callbacks, Negotiate options and enum/composite type mappings are write-only on
    /// <see cref="NpgsqlDataSourceBuilder"/> and unreadable from the built source, so forking one
    /// would silently drop the host's authentication. The host builds this one, so nothing is copied
    /// and nothing is lost. Configure it exactly as the DbContext's, then pass it here.
    /// </para>
    /// <para>
    /// Left null on the data-source path, locks keep sharing the DbContext's pool and its connections
    /// keep paying the seven-statement reset — measured at 17.00 statements per acquire/release
    /// against 11.00 with a pool of their own. Left null on the connection-string path it is simply
    /// unused: Warp derives the second pool there itself, losslessly, and this parameter is not needed.
    /// </para>
    /// </param>
    public static IWarpBuilder<TContext> UsePostgreSql<TContext>(this IWarpBuilder<TContext> builder, NpgsqlDataSource? lockDataSource)
        where TContext : DbContext
    {
        builder.Services.TryAddSingleton<IWarpSqlQueries<TContext>>(sp =>
        {
            using var scope = sp.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();

            // Propagate the configured lease TTL so the heartbeat SQL renewal window matches
            // what SingletonServiceStrategy uses when it first acquires the lease.
            var workerConfig = sp.GetService<IOptions<WarpServerConfiguration>>();
            var leaseTtl = workerConfig?.Value.BackgroundServiceLeaseTtl ?? WarpServerConfiguration.DefaultBackgroundServiceLeaseTtl;
            var names = WarpJobTableNames.FromModel(context.Model, (int)leaseTtl.TotalSeconds);

            return new PostgresWarpSqlQueries<TContext>(names);
        });

        builder.Services.TryAddSingleton<IDatabaseExceptionClassifier, PostgresExceptionClassifier>();

        builder.Services.TryAddSingleton<IWarpNotificationTransportFactory>(sp =>
            new PostgresNotificationTransportFactory(ResolveDataSource<TContext>(sp)));

        builder.Services.TryAddSingleton<IWarpLockProvider>(sp => CreateLockProvider<TContext>(sp, lockDataSource));

        builder.Services.TryAddSingleton<IWarpSemaphoreProvider>(sp => CreateSemaphoreProvider<TContext>(sp, lockDataSource));

        // Points the Warp server context at the same database as TContext (data source if the user
        // registered one, else the connection string), inheriting auth/SSL settings.
        builder.Services.TryAddSingleton<IWarpServerContextConfigurator>(new PostgresServerContextConfigurator<TContext>());

        return builder;
    }

    /// <summary>
    /// Three sources for the lock connection, in precedence order: the host's dedicated lock data
    /// source, then <typeparamref name="TContext"/>'s own data source (shared pool — the path Warp
    /// cannot split for itself), then the derived lock connection string (its own pool).
    /// </summary>
    private static PostgresLockProvider CreateLockProvider<TContext>(IServiceProvider sp, NpgsqlDataSource? lockDataSource)
        where TContext : DbContext
    {
        var logger = sp.GetRequiredService<ILogger<PostgresLockProvider>>();

        if (lockDataSource is not null)
        {
            return new PostgresLockProvider(lockDataSource, logger);
        }

        if (ResolveDataSource<TContext>(sp) is { } contextDataSource)
        {
            return new PostgresLockProvider(contextDataSource, logger);
        }

        return new PostgresLockProvider(ResolveLockConnectionString<TContext>(sp), logger);
    }

    /// <summary>The semaphore half of <see cref="CreateLockProvider{TContext}"/> — same precedence, same reasons.</summary>
    private static PostgresSemaphoreProvider CreateSemaphoreProvider<TContext>(IServiceProvider sp, NpgsqlDataSource? lockDataSource)
        where TContext : DbContext
    {
        var logger = sp.GetRequiredService<ILogger<PostgresSemaphoreProvider>>();

        if (lockDataSource is not null)
        {
            return new PostgresSemaphoreProvider(lockDataSource, logger);
        }

        if (ResolveDataSource<TContext>(sp) is { } contextDataSource)
        {
            return new PostgresSemaphoreProvider(contextDataSource, logger);
        }

        return new PostgresSemaphoreProvider(ResolveLockConnectionString<TContext>(sp), logger);
    }

    /// <summary>
    /// The lock providers' connection string: <typeparamref name="TContext"/>'s, with
    /// <c>Application Name</c> suffixed so the advisory-lock sessions land in their own Npgsql pool.
    /// <para>
    /// This is a measured cost, not tidiness. Npgsql resets a pooled connection with one
    /// <c>DISCARD ALL</c>, but any connector carrying prepared statements must instead be reset with a
    /// seven-statement sequence (<c>CLOSE ALL</c>, <c>UNLISTEN *</c>, <c>SELECT pg_advisory_unlock_all()</c>,
    /// <c>RESET ALL</c>, <c>DISCARD TEMP</c>, <c>DISCARD SEQUENCES</c>,
    /// <c>SET SESSION AUTHORIZATION DEFAULT</c>), because <c>DISCARD ALL</c> would deallocate them.
    /// Medallion prepares its advisory-lock statements, and when it is handed the same connection
    /// string as the DbContext both share one pool — so its prepared connectors flip EVERY connection
    /// in the process, and ordinary EF round trips start paying the seven-statement reset too.
    /// Measured on the concurrency addon's no-contention arm: 5.41 resets per job at 7 statements each,
    /// ~38 statements per job of pure connection hygiene, with <c>DISCARD ALL</c> gone from the trace
    /// entirely. The lab's <c>lockprobe</c> arm isolates it — 17.00 statements per acquire/release
    /// against 11.00 with the pool split.
    /// </para>
    /// <para>
    /// The user's own <c>Application Name</c> is preserved and suffixed rather than replaced: hosts
    /// key monitoring and <c>pg_stat_activity</c> dashboards off it, and the suffix makes the lock
    /// sessions legible there rather than anonymous.
    /// </para>
    /// <para>
    /// <b>The data-source path is deliberately NOT split.</b> An <see cref="NpgsqlDataSource"/> carries
    /// host-owned authentication and encryption configuration (periodic password refresh, client
    /// certificates) that cannot be copied onto a forked data source, and silently dropping those to
    /// save statements would trade correctness for throughput. Hosts registering a data source keep
    /// the shared pool and the seven-statement reset. See §2.16.
    /// </para>
    /// <para>
    /// Operational note: two pools mean two <c>MaxPoolSize</c> ceilings against one server, so a host
    /// sized right at its server's <c>max_connections</c> must account for the second pool — what was
    /// one bound covering EF and the lock sessions together is now that bound twice. Active use should
    /// be close to unchanged (the lock sessions moved rather than multiplied, and Medallion multiplexes
    /// locks onto a shared connection), and <c>MinPoolSize</c> is pinned to 0 below so a host that
    /// pre-warms its DbContext pool does not silently hold those idle connections twice over. The
    /// ceiling is inherited rather than capped: nothing measured justifies a particular number, and an
    /// invented one would be the wrong kind of constant to add.
    /// </para>
    /// </summary>
    internal static string ResolveLockConnectionString<TContext>(IServiceProvider sp)
        where TContext : DbContext
    {
        var connectionString = ResolveConnectionString<TContext>(sp);
        var seed = new NpgsqlConnectionStringBuilder(connectionString);

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = BuildLockApplicationName(seed.ApplicationName),

            // Explicitly 0, never the host's value. MinPoolSize is a per-pool floor, so inheriting a
            // pre-warmed DbContext pool's setting would hold that many idle connections a SECOND time
            // for locks that mostly need one. Npgsql's own default is 0; this only overrides a host
            // that set it.
            MinPoolSize = 0,
        }.ConnectionString;
    }

    /// <summary>
    /// Suffixes the host's <c>Application Name</c>, truncating the host's own portion when the result
    /// would not survive the server.
    /// <para>
    /// Postgres stores <c>application_name</c> in a <c>NAMEDATALEN-1</c> (63 byte) field and truncates
    /// silently past it. Appending to a name already near that length would drop the suffix server-side
    /// and the lock sessions would appear in <c>pg_stat_activity</c> under a string indistinguishable
    /// from the DbContext's — defeating the legibility the suffix exists for, and making an exact-match
    /// query for them find nothing. Client-side pool separation is unaffected either way (Npgsql keys
    /// the pool on the full connection string, not on what the server stored), so this is purely about
    /// what an operator sees.
    /// </para>
    /// </summary>
    private static string BuildLockApplicationName(string? hostApplicationName)
    {
        if (string.IsNullOrEmpty(hostApplicationName))
        {
            return LockApplicationName;
        }

        var suffix = $":{LockApplicationName}";
        var room = MaxApplicationNameLength - suffix.Length;
        var host = hostApplicationName.Length > room ? hostApplicationName[..room] : hostApplicationName;

        return host + suffix;
    }

    internal static string ResolveConnectionString<TContext>(IServiceProvider sp)
        where TContext : DbContext
    {
        using var scope = sp.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<TContext>>();
        var relationalExtension = dbOptions.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
        var connectionString = relationalExtension?.ConnectionString;

        if (connectionString is null)
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Cannot resolve connection string for Warp PostgreSQL provider.");
        }

        return connectionString;
    }

    // When the user registered the DbContext with UseNpgsql(NpgsqlDataSource) — e.g. via
    // Aspire's AddAzureNpgsqlDataSource, or any setup that needs Managed Identity tokens
    // or SSL settings attached to the data source — surface that data source so our lock,
    // semaphore, and notification connections inherit the same auth/encryption configuration
    // instead of being opened from a raw connection string that may be missing them.
    internal static NpgsqlDataSource? ResolveDataSource<TContext>(IServiceProvider sp)
        where TContext : DbContext
    {
        // AddDbContext registers DbContextOptions<TContext> as Scoped (only AddDbContextPool
        // makes it Singleton), so we have to resolve it through a scope — otherwise providers
        // built with ValidateScopes=true reject the resolution from the root provider.
        using var scope = sp.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<TContext>>();

        // EF1001: NpgsqlOptionsExtension is in an Infrastructure.Internal namespace, but it's the
        // documented extension point exposing the DataSource bound to a DbContext — there is no
        // public alternative. Same pattern Aspire's Npgsql component uses to read this back.
#pragma warning disable EF1001
        var optionsDataSource = dbOptions.Extensions
            .OfType<NpgsqlOptionsExtension>()
            .FirstOrDefault()?.DataSource as NpgsqlDataSource;
#pragma warning restore EF1001

        // Prefer the data source EF actually uses for TContext (set only when the DbContext was configured
        // with UseNpgsql(dataSource)). When it's null — the DbContext was configured with a bare connection
        // string, or UseNpgsql() with the data source resolved from DI — fall back to a DI-registered
        // NpgsqlDataSource (Aspire's AddNpgsqlDataSource / AddAzureNpgsqlDataSource, or a manual
        // AddNpgsqlDataSource). This lets Warp's lock / semaphore / notification / server-context connections
        // inherit that data source's auth + SSL (RDS IAM tokens, Cloud SQL, client certs) instead of opening
        // from a raw connection string that lacks them. Without it, a DI-only data source was silently ignored.
        return optionsDataSource ?? sp.GetService<NpgsqlDataSource>();
    }
}

internal sealed class PostgresServerContextConfigurator<TContext> : IWarpServerContextConfigurator
    where TContext : DbContext
{
    private readonly Lock _resolveLock = new();
    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;
    private bool _resolved;

    // The server context's options are Scoped (AddDbContext's (sp, options) overload registers them
    // that way), so this runs on EVERY scope that resolves IWarpServerContext — and resolving the
    // connection source creates a nested scope to read the user's DbContextOptions. ServerTaskLoop
    // opens a scope per bookkeeping call, once per tick, per server task, so that cost multiplies.
    // The source cannot change at runtime, so resolve it once (under a lock — server startup fires
    // many loops' first scopes concurrently, and a null data-source probe must cache too).
    public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
    {
        lock (_resolveLock)
        {
            if (!_resolved)
            {
                _dataSource = PostgreSqlServiceConfiguration.ResolveDataSource<TContext>(applicationServices);
                if (_dataSource is null)
                {
                    _connectionString = PostgreSqlServiceConfiguration.ResolveConnectionString<TContext>(applicationServices);
                }

                _resolved = true;
            }
        }

        if (_dataSource is not null)
        {
            optionsBuilder.UseNpgsql(_dataSource);

            return;
        }

        optionsBuilder.UseNpgsql(_connectionString);
    }
}
