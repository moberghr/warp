using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Warp.Core;

public static class WarpBuilderExtensions
{
    /// <summary>
    /// Excludes handlers (IRequestHandler, IJobHandler, IMessageHandler, IStreamRequestHandler)
    /// defined in the given assembly from the host's DI graph. The Warp source generator
    /// discovers handlers transitively across project references and auto-registers them,
    /// which is convenient in single-host solutions but causes scope-validation pain in
    /// multi-host solutions where each host should only resolve a subset of handlers.
    /// Use this to opt specific assemblies out without rewriting the discovery logic.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddWarp&lt;AppDbContext&gt;(opt =>
    /// {
    ///     opt.ExcludeHandlersFromAssembly(typeof(BackOfficeMarker).Assembly);
    /// });
    /// </code>
    /// </example>
    public static IWarpBuilder ExcludeHandlersFromAssembly(this IWarpBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assembly);

        builder.Configuration.ExcludedHandlerAssemblies.Add(assembly);
        return builder;
    }

    /// <summary>
    /// Binds a configuration section onto the builder's config fields (inherited from
    /// <see cref="WarpConfiguration"/> / <c>WarpWorkerConfiguration</c>). Intended for the
    /// appsettings.json pattern:
    /// <code>
    /// services.AddWarpWorker&lt;AppDbContext&gt;(opt =>
    /// {
    ///     opt.BindConfiguration(builder.Configuration.GetSection("Warp"));
    ///     opt.UsePostgreSql();
    /// });
    /// </code>
    /// Call order matters if both bind and explicit property assignment are used — the last
    /// write wins. Provider opt-in (<c>UsePostgreSql()</c> / <c>UseSqlServer()</c>) must still
    /// be called explicitly; it's a DI service registration, not a config field.
    /// </summary>
    public static IWarpBuilder<TContext> BindConfiguration<TContext>(
        this IWarpBuilder<TContext> builder,
        IConfiguration section)
        where TContext : DbContext
    {
        section.Bind(builder.Configuration);
        return builder;
    }
}
