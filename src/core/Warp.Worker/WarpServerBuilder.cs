using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;

namespace Warp.Worker;

/// <summary>
/// Builder passed to <see cref="ServiceConfiguration.AddWarpServer{TContext}"/>. Inherits
/// <see cref="WarpServerConfiguration"/> so config fields are set directly on the builder,
/// and implements <see cref="IWarpBuilder{TContext}"/> so every Core addon extension
/// (AddRetry, AddConcurrency, AddCircuitBreaker, AddNoRestart, AddBackgroundService, and
/// provider extensions like UsePostgreSql) can be called from inside the AddWarpServer lambda.
/// The worker is a component of the server — on by default, opt out with
/// <see cref="WarpServerConfiguration.DisableWorker"/>.
/// </summary>
public sealed class WarpServerBuilder<TContext> : WarpServerConfiguration, IWarpBuilder<TContext>
    where TContext : DbContext
{
    public WarpServerBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    WarpConfiguration IWarpBuilder.Configuration => this;
}
