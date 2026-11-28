using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

/// <summary>
/// Points the Warp server context at the same database as the user's <c>TContext</c>. Implemented
/// by the provider package and registered by <c>opt.UsePostgreSql()</c> / <c>opt.UseSqlServer()</c>.
/// The server context is provider-agnostic; the provider supplies the connection (string or data
/// source) pulled from <c>TContext</c>'s registered options, plus any provider tuning.
/// </summary>
public interface IWarpServerContextConfigurator
{
    void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices);

    /// <summary>
    /// Whether this provider can open Warp's own context for an application context configured with
    /// <paramref name="applicationOptions"/>: false when the application's context runs on another provider,
    /// for example EF Core in-memory in the application's tests. Warp's dashboard and admin services then run
    /// on the application's context instead. The default is <see langword="true"/>.
    /// </summary>
    /// <param name="applicationOptions">The application context's options as the application configured them.</param>
    /// <returns><see langword="true"/> when Warp's own context can use this provider.</returns>
    bool Supports(DbContextOptions applicationOptions) => true;
}
