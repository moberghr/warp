using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core;

/// <summary>
/// Non-generic surface for addon extensions that don't need the user's DbContext type.
/// Addons that DO need it target <see cref="IWarpBuilder{TContext}"/>.
/// </summary>
public interface IWarpBuilder
{
    IServiceCollection Services { get; }

    WarpConfiguration Configuration { get; }
}

/// <summary>
/// Common surface implemented by both <see cref="WarpBuilder{TContext}"/> (Core-only) and
/// the worker-side builder defined in the Warp.Worker package. Addon extension methods
/// that need to bake <typeparamref name="TContext"/> into a generic registration
/// (AddSagas, AddConcurrency, AddCircuitBreaker, UsePostgreSql, UseSqlServer) target
/// this interface; addons that don't (AddBackgroundService) target the non-generic base.
/// </summary>
public interface IWarpBuilder<TContext> : IWarpBuilder
    where TContext : DbContext;

/// <summary>
/// Builder passed to <see cref="ServiceConfiguration.AddWarp{TContext}"/>. Inherits
/// <see cref="WarpConfiguration"/> so config fields are set directly on the builder, and
/// exposes <see cref="Services"/> so addon extension methods can register their own services.
/// The builder instance is registered as the <c>IOptions&lt;WarpConfiguration&gt;</c> value,
/// so values set here become the runtime configuration with no extra copy step.
/// </summary>
public sealed class WarpBuilder<TContext> : WarpConfiguration, IWarpBuilder<TContext>
    where TContext : DbContext
{
    public WarpBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    WarpConfiguration IWarpBuilder.Configuration => this;
}
