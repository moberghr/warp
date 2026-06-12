using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.Services;

/// <summary>
/// Server-level admin commands. <c>PauseServer</c> / <c>PauseWorkerGroup</c> only stamp
/// <c>PausedAt</c> on the DB row; pause does not propagate instantly. Each server's
/// <c>Heartbeat</c> task reads the new value on its next tick (cadence
/// <c>WarpServerConfiguration.HealthCheckInterval</c>, default 3s) and refreshes its
/// in-memory <c>PauseStateHolder</c>; only then will workers on that server stop
/// claiming new jobs. An iteration that already passed its pause check before the
/// holder flipped will still complete its in-flight claim. Treat pause as "no new
/// fetches after up to one heartbeat", not as a synchronous barrier.
/// </summary>
public interface IServerCommandService
{
    Task<bool> PauseServer(Guid serverId);

    Task<bool> ResumeServer(Guid serverId);

    Task<bool> PauseWorkerGroup(Guid workerGroupId);

    Task<bool> ResumeWorkerGroup(Guid workerGroupId);
}

public class ServerCommandService<TContext> : IServerCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    public ServerCommandService(TContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<bool> PauseServer(Guid serverId)
    {
        var server = await _context.Set<Server>().FindAsync(serverId);
        if (server == null)
        {
            return false;
        }

        server.PausedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResumeServer(Guid serverId)
    {
        var server = await _context.Set<Server>().FindAsync(serverId);
        if (server == null)
        {
            return false;
        }

        server.PausedAt = null;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> PauseWorkerGroup(Guid workerGroupId)
    {
        var group = await _context.Set<WorkerGroup>().FindAsync(workerGroupId);
        if (group == null)
        {
            return false;
        }

        group.PausedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResumeWorkerGroup(Guid workerGroupId)
    {
        var group = await _context.Set<WorkerGroup>().FindAsync(workerGroupId);
        if (group == null)
        {
            return false;
        }

        group.PausedAt = null;
        await _context.SaveChangesAsync();
        return true;
    }
}
