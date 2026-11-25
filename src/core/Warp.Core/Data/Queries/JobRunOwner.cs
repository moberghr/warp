using Warp.Core.Entities;

namespace Warp.Core.Data.Queries;

/// <summary>
/// Who holds one run of a job, for the writes a worker makes to it while and after it runs
/// (<see cref="IWarpSqlQueries{TContext}.CompleteJobsAsync"/>,
/// <see cref="IWarpSqlQueries{TContext}.RenewRunningJobsAsync"/>): the worker running it, and the claim that
/// handed it over, as the claim stamped the row (<c>CurrentWorkerId</c> and <c>LastKeepAlive</c>). Such a
/// write touches the row only while it is still <c>Processing</c> and still this run's: carrying the run's
/// <paramref name="RunId"/>, and stamped by <paramref name="WorkerId"/> (a renewal has run) or still exactly
/// as the claim left it. Once <c>StaleJobRecovery</c> has taken the job back and another claim has it, the
/// old run writes nothing, even when the new claim reached the same worker (#347): the run id tells two runs
/// on one worker apart. The worker/keep-alive pair stays in the guard for rolling deploys, where a binary
/// that predates the run id can take a job back without clearing it.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="WorkerId">The worker running it.</param>
/// <param name="ClaimedBy">The <c>CurrentWorkerId</c> the claim stamped: the worker group in dispatcher mode, the worker otherwise.</param>
/// <param name="ClaimedKeepAlive">The <c>LastKeepAlive</c> the claim stamped.</param>
/// <param name="RunId">The <c>RunId</c> the claim stamped.</param>
public readonly record struct JobRunOwner(Guid JobId, Guid WorkerId, Guid? ClaimedBy, DateTime? ClaimedKeepAlive, Guid? RunId = null)
{
    /// <summary>The run of a job as its claim handed it to <paramref name="workerId"/>: read before the worker changes the row.</summary>
    public static JobRunOwner Of(Job job, Guid workerId)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobRunOwner(job.Id, workerId, job.CurrentWorkerId, job.LastKeepAlive, job.RunId);
    }
}
