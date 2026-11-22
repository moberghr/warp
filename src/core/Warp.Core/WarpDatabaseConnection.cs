using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core;

/// <summary>
/// Where <typeparamref name="TContext"/> connects, resolved once and kept for the life of the process.
/// Anything Warp opens beside the DbContext (lock and semaphore pools, the push transport, the server
/// context) reads it from here instead of rebuilding the context's options each time.
/// <para>
/// <see cref="Options"/> are the options as the application configured them, BEFORE Warp adds its
/// interceptors and model customizer. Reading a connection string therefore never constructs Warp's
/// interceptors, which is what let the push transport and the enqueue interceptor wait on each other
/// at startup (#322).
/// </para>
/// <para>Registered by <c>AddWarp</c>; resolvable in any process that calls it.</para>
/// </summary>
public sealed class WarpDatabaseConnection<TContext>
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<IServiceProvider, DbContextOptions<TContext>> _buildOptions;
    private readonly Lazy<DbContextOptions<TContext>> _options;
    private readonly Lazy<string> _connectionString;

    internal WarpDatabaseConnection(IServiceScopeFactory scopeFactory, Func<IServiceProvider, DbContextOptions<TContext>> buildOptions)
    {
        _scopeFactory = scopeFactory;
        _buildOptions = buildOptions;
        _options = new Lazy<DbContextOptions<TContext>>(BuildOptions, LazyThreadSafetyMode.ExecutionAndPublication);
        _connectionString = new Lazy<string>(ResolveConnectionString, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The application's own options for <typeparamref name="TContext"/>, without Warp's
    /// additions. Read provider extensions from them (for example the Npgsql data source); do not build
    /// a context on them — they were built in a scope that has since been disposed, and EF resolves its
    /// logger factory from that scope.</summary>
    public DbContextOptions<TContext> Options => _options.Value;

    /// <summary>The connection string <typeparamref name="TContext"/> uses. Taken from the options,
    /// or, when they carry none (a context configured with a data source), from a short-lived context
    /// built on them.</summary>
    public string ConnectionString => _connectionString.Value;

    private DbContextOptions<TContext> BuildOptions()
    {
        // AddDbContext registers its options Scoped, so build them inside a scope even though the
        // result is kept: the options object holds no scoped state once built.
        using var scope = _scopeFactory.CreateScope();

        return _buildOptions(scope.ServiceProvider);
    }

    private string ResolveConnectionString()
    {
        var fromOptions = Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .FirstOrDefault()
            ?.ConnectionString;

        if (!string.IsNullOrEmpty(fromOptions))
        {
            return fromOptions;
        }

        // A context needs options built in a LIVE scope (EF keeps the application service provider they
        // were built with), so rebuild them here rather than reuse the cached Options.
        using var scope = _scopeFactory.CreateScope();
        using var context = ActivatorUtilities.CreateInstance<TContext>(scope.ServiceProvider, _buildOptions(scope.ServiceProvider));

        return context.Database.GetConnectionString()
            ?? throw new InvalidOperationException($"Cannot resolve the connection string for {typeof(TContext).Name}.");
    }
}
