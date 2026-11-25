namespace Warp.Worker.Services;

/// <summary>
/// A server task that behaves differently on a run its schedule started than on one a signal started
/// (the Orchestrator sweeps every parent on a scheduled run). Internal on purpose: <see cref="IServerTask"/>
/// stays as it is. <see cref="ServerTaskRunner{TContext}"/> sets it before every run.
/// </summary>
internal interface IScheduledRunAware
{
    /// <summary>Called before the run: true when the scheduler claimed it because the task's scheduled time had come.</summary>
    void BeginRun(bool scheduled);
}
