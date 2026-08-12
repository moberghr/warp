namespace Warp.Worker;

/// <summary>
/// Thread-safe singleton that holds the current pause state for this server.
/// Workers consult this on every poll iteration before fetching a job.
/// Uses an immutable snapshot swapped atomically to avoid torn reads.
/// <para>
/// <b>Pause is not instantaneous.</b> The holder is only refreshed by the periodic
/// <see cref="Services.Heartbeat{TContext}"/> task (cadence:
/// <see cref="WarpServerConfiguration.HealthCheckInterval"/>, default 5s). Once a
/// caller writes <c>PausedAt</c> to <see cref="Warp.Core.Data.Entities.Server"/> /
/// <see cref="Warp.Core.Data.Entities.WorkerGroup"/>, every server's worker pool keeps
/// fetching until that server's next heartbeat tick reads the new state and updates its
/// own holder. In addition, an iteration that already passed its <see cref="IsPaused"/>
/// check before the holder flipped will still complete its claim. Callers that need
/// hard "no further job starts" semantics (e.g. drain-before-deploy) must combine
/// pause with a wait that's at least <c>HealthCheckInterval + PollingInterval</c>
/// before assuming workers are quiesced.
/// </para>
/// </summary>
public class PauseStateHolder
{
    private sealed record PauseSnapshot(bool ServerPaused, Dictionary<Guid, bool> GroupPaused);

    private volatile PauseSnapshot _state = new(false, []);

    public bool IsPaused(Guid? workerGroupId)
    {
        var snapshot = _state;
        return snapshot.ServerPaused
            || (workerGroupId.HasValue
                && snapshot.GroupPaused.TryGetValue(workerGroupId.Value, out var paused)
                && paused);
    }

    public void Update(bool serverPaused, Dictionary<Guid, bool> groupPaused)
    {
        _state = new PauseSnapshot(serverPaused, groupPaused);
    }
}
