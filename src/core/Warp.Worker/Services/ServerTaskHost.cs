using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Events;

namespace Warp.Worker.Services;

/// <summary>
/// Single <see cref="BackgroundService"/> that drives every registered
/// <see cref="IServerTask"/>. Replaces the per-task <see cref="BackgroundService"/>
/// subclasses: tasks themselves become plain DI-registered services, and the host owns
/// the loop, the lock, and the ServerTask/ServerLog bookkeeping via
/// <see cref="ServerTaskLoop{TContext}"/>.
/// </summary>
/// <remarks>
/// A task with <see cref="IServerTask.DefaultInterval"/> equal to <c>null</c> is not
/// given an auto-run loop — the host logs it and moves on. The task stays registered
/// in DI so other code can still resolve it.
/// </remarks>
public sealed class ServerTaskHost<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly Dictionary<Type, ServerTaskLoop<TContext>> _loops = [];
    private readonly List<IDisposable> _signalSubscriptions = [];
    private readonly IServiceScopeFactory _scopes;
    private readonly Guid _serverId;
    private readonly ILogger _logger;

    public ServerTaskHost(
        IServiceScopeFactory scopes,
        IWarpLockProvider lockProvider,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        IOptions<WarpServerConfiguration> configuration,
        ServerTaskSignals<TContext> signals)
    {
        var hostLogger = loggerFactory.CreateLogger<ServerTaskHost<TContext>>();
        _scopes = scopes;
        _serverId = configuration.Value.ServerId;
        _logger = hostLogger;

        using var metadataScope = scopes.CreateScope();
        foreach (var task in metadataScope.ServiceProvider.GetServices<IServerTask>())
        {
            var type = task.GetType();
            if (_loops.ContainsKey(type))
            {
                continue;
            }

            if (task.DefaultInterval == null)
            {
                hostLogger.LogInformation(
                    "Server task {Name} has null DefaultInterval — auto-run loop disabled.",
                    task.Name);
                continue;
            }

            var loopLogger = loggerFactory.CreateLogger($"Warp.Worker.Services.ServerTaskLoop[{task.Name}]");
            _loops[type] = new ServerTaskLoop<TContext>(
                task,
                scopes,
                lockProvider,
                time,
                configuration.Value.ServerId,
                loopLogger,
                configuration.Value.ServerTaskSignalInterval);
        }

        foreach (var loop in _loops.Values)
        {
            foreach (var channel in loop.Signals)
            {
                _signalSubscriptions.Add(signals.Subscribe(channel, loop.Signal));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_loops.Count == 0)
        {
            return;
        }

        await RegisterAllAsync(stoppingToken);

        var tasks = _loops.Values.Select(loop => loop.RunAsync(stoppingToken));
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Test-only. Runs <typeparamref name="TTask"/> once under its lock + scope with no
    /// bookkeeping, no retries, no log rows — and exceptions propagate.
    /// </summary>
    internal Task<string?> RunOnceAsync<TTask>(CancellationToken ct)
        where TTask : IServerTask
    {
        if (!_loops.TryGetValue(typeof(TTask), out var loop))
        {
            throw new InvalidOperationException(
                $"No ServerTaskLoop registered for {typeof(TTask).Name}. " +
                "Either the task is not registered as IServerTask, or its DefaultInterval is null.");
        }

        return loop.RunOnceAsync(ct);
    }

    // Registers every task's ServerTask row for this server in one round trip: one read of the server's
    // rows, one save that inserts the missing ones (EF batches them into one command). Each loop used to
    // register itself on its first tick, so a server starting fourteen tasks opened fourteen scopes and
    // sent fourteen SELECT + INSERT pairs at once, and several servers starting together contended on
    // them. A failure here is logged and left to the loops, which register themselves as before.
    private async Task RegisterAllAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

            var existing = await context.Set<ServerTask>()
                .Where(x => x.ServerId == _serverId)
                .ToListAsync(ct);

            var rows = new List<(ServerTaskLoop<TContext> Loop, ServerTask Row)>(_loops.Count);
            foreach (var loop in _loops.Values)
            {
                var row = existing.Find(x => string.Equals(x.TaskName, loop.Name, StringComparison.Ordinal));
                if (row == null)
                {
                    row = new ServerTask { ServerId = _serverId, TaskName = loop.Name };
                    context.Set<ServerTask>().Add(row);
                }

                row.IntervalSeconds = loop.DefaultInterval.TotalSeconds;
                rows.Add((loop, row));
            }

            await context.SaveChangesAsync(ct);

            foreach (var (loop, row) in rows)
            {
                loop.MarkRegistered(row.Id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registering the server's tasks in one batch failed; each task registers itself");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var subscription in _signalSubscriptions)
        {
            subscription.Dispose();
        }

        foreach (var loop in _loops.Values)
        {
            loop.Dispose();
        }
    }
}
