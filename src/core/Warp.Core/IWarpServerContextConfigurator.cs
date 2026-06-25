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
}
