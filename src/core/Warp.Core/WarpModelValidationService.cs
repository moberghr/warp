using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Warp.Core;

// Fail-fast model check at host startup: confirms the Warp EF model is present on TContext before
// any worker, server task, or request handler runs, so a missing AddWarp / ApplyWarpModel surfaces
// at boot with a clear message instead of on the first publish. Plain IHostedService (not
// BackgroundService) so the host awaits StartAsync to completion before the app starts. The
// Publisher / BatchPublisher constructor guard remains the backstop for non-hosted (raw
// ServiceProvider) usage that never starts an IHost.
internal sealed class WarpModelValidationService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WarpModelValidationService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        WarpModelGuard.EnsureWarpModelApplied(context);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
