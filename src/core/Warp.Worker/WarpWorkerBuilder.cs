using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;

namespace Warp.Worker;

/// <summary>
/// Builder passed to <see cref="ServiceConfiguration.AddWarpWorker{TContext}"/>. Inherits
/// <see cref="WarpWorkerConfiguration"/> so config fields are set directly on the builder,
/// and implements <see cref="IWarpBuilder{TContext}"/> so every Core addon extension
/// (AddMutex, AddRetry, AddCircuitBreaker, AddNoRestart, and future provider extensions like
/// UsePostgreSql) can be called from inside the AddWarpWorker lambda.
/// </summary>
public sealed class WarpWorkerBuilder<TContext> : WarpWorkerConfiguration, IWarpBuilder<TContext>
    where TContext : DbContext
{
    public WarpWorkerBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    WarpConfiguration IWarpBuilder.Configuration => this;
}
