using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Warp.Core;

// Fail-fast model check at host startup: confirms the Warp EF model is present on TContext AND that
// no host-added runtime convention retyped Warp's columns at model finalization (past the build-time
// ownership pass in ApplyWarpModel), before any worker, server task, or request handler runs — so
// both surface at boot with a clear message instead of as per-tick failures. Plain IHostedService (not
// BackgroundService) so the host awaits StartAsync to completion before the app starts. The
// Publisher / BatchPublisher constructor guard remains the backstop for non-hosted (raw
// ServiceProvider) usage that never starts an IHost.
//
// The storage contract is about SQL column types, so it is skipped when TContext runs on a non-relational
// provider (EF in-memory in an application's own test suite, see "Testing apps that use Warp"). The model
// check is provider-agnostic and still runs.
internal sealed class WarpModelValidationService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WarpModelValidationService<TContext>> _logger;

    public WarpModelValidationService(IServiceScopeFactory scopeFactory, ILogger<WarpModelValidationService<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        WarpModelGuard.EnsureWarpModelApplied(context);

        if (!context.Database.IsRelational())
        {
            _logger.LogDebug(
                "Skipping Warp's storage-contract check for {Context}: its provider ({Provider}) is not relational.",
                typeof(TContext).Name,
                context.Database.ProviderName);

            return Task.CompletedTask;
        }

        WarpModelGuard.EnsureWarpStorageContract(context);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
