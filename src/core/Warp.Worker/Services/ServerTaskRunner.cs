using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Events;

namespace Warp.Worker.Services;

/// <summary>What the scheduler needs to know about a server task, read once from its registration.</summary>
internal sealed record ServerTaskInfo(
    Type TaskType,
    string Name,
    string? LockKey,
    bool LocksWithTransaction,
    TimeSpan Interval,
    bool RerunImmediately,
    bool LogOnSuccess,
    IReadOnlyList<ServerTaskSignal> Signals)
{
    /// <summary>A task every server runs for itself (the heartbeat): no lock key, so no lease.</summary>
    public bool IsLocal => LockKey == null;

    public static ServerTaskInfo From(IServerTask task) =>
        new(
            task.GetType(),
            task.Name,
            task.LockKey,
            task.LocksWithTransaction,
            task.DefaultInterval ?? TimeSpan.Zero,
            task.RerunImmediately,
            task.LogOnSuccess,
            [.. task.Signals]);
}

/// <summary>
/// Runs one server task once, in its own scope, with the commit boundary the task declares. The one
/// place that knows how a task is executed: the scheduler calls it for a claimed task, and tests call it
/// through <c>ServerTaskHost.RunOnceAsync</c>.
/// <list type="bullet">
/// <item>A task that <see cref="IServerTask.LocksWithTransaction"/> runs inside one transaction, and
/// <see cref="IServerTask.OnCommittedAsync"/> runs after the commit.</item>
/// <item>A task that manages its own transactions (the message router) or has no lock key (the heartbeat)
/// is just executed, and the hook runs after it.</item>
/// </list>
/// Which server may run a task is not decided here: the lease is (rule 6.20).
/// </summary>
internal sealed class ServerTaskRunner<TContext>
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LegacyTaskLock<TContext> _legacyLock;

    public ServerTaskRunner(IServiceScopeFactory scopes, IWarpLockProvider lockProvider)
    {
        _scopes = scopes;
        _legacyLock = new LegacyTaskLock<TContext>(lockProvider);
    }

    /// <summary>
    /// Runs the task once. <paramref name="underLegacyLock"/> also takes the lock servers from before the
    /// lease take (see <see cref="LegacyTaskLock{TContext}"/>); <c>Ran</c> is false only when that lock
    /// was held elsewhere. Exceptions from the task propagate, and the hook is then not called.
    /// </summary>
    public async Task<(bool Ran, string? Message)> RunAsync(ServerTaskInfo info, bool underLegacyLock, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var task = scope.ServiceProvider
            .GetServices<IServerTask>()
            .First(x => x.GetType() == info.TaskType);

        if (underLegacyLock && info.LockKey != null)
        {
            return await _legacyLock.RunAsync(scope.ServiceProvider, task, info, ct);
        }

        if (info.IsLocal || !info.LocksWithTransaction)
        {
            var message = await task.ExecuteAsync(ct);
            await task.OnCommittedAsync(ct);

            return (true, message);
        }

        // One transaction, as the task had under its lock: every statement of a pass commits together.
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        string? result;
        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            result = await task.ExecuteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await task.OnCommittedAsync(ct);

        return (true, result);
    }
}

/// <summary>
/// The lock a cluster-wide task took on every server before leases (rule 6.20), kept for rolling deploys
/// only. While a server from before the lease is alive it still takes this lock every interval, so a new
/// server takes it too around the same run and the two never run a task at once.
/// <para>
/// REMOVE in the release after 8.x, together with <c>Server.CoordinationVersion</c>'s use in the claim
/// statement and the <c>underLegacyLock</c> argument: once no pre-lease server can be alive in a cluster,
/// nothing takes this lock any more.
/// </para>
/// </summary>
internal sealed class LegacyTaskLock<TContext>
    where TContext : DbContext
{
    private readonly IWarpLockProvider _lockProvider;

    public LegacyTaskLock(IWarpLockProvider lockProvider) => _lockProvider = lockProvider;

    public async Task<(bool Ran, string? Message)> RunAsync(
        IServiceProvider services,
        IServerTask task,
        ServerTaskInfo info,
        CancellationToken ct)
    {
        if (info.LocksWithTransaction)
        {
            // Transaction-scoped: the lock, the task's writes and the commit are one transaction.
            var context = services.GetRequiredService<IWarpServerContext>().Context;
            var queries = services.GetRequiredService<IWarpSqlQueries<TContext>>();
            var outcome = await queries.RunUnderTransactionLockAsync<string?>(
                context,
                info.LockKey!,
                async (_, innerCt) => await task.ExecuteAsync(innerCt),
                ct);
            if (!outcome.LockHeld)
            {
                return (false, null);
            }

            await task.OnCommittedAsync(ct);

            return (true, outcome.Result);
        }

        // Session-scoped, for a task whose work spans several transactions.
        var handle = await _lockProvider.TryAcquireAsync(info.LockKey!, TimeSpan.Zero, ct);
        if (handle == null)
        {
            return (false, null);
        }

        try
        {
            var message = await task.ExecuteAsync(ct);
            await task.OnCommittedAsync(ct);

            return (true, message);
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }
}
