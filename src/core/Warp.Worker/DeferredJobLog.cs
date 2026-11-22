using Warp.Core.Data.Entities;

namespace Warp.Worker;

/// <summary>
/// The "Processing" log row of a running job, waiting to be written with something that is going to
/// the database anyway (rule 6.12): the job monitor's first flush, for a job that runs long enough to
/// have one, or the job's finalizing write. Written on its own it cost every job a statement and a
/// commit before its handler started.
/// <para>
/// The monitor and the finalizing code both ask for it, and whoever asks first gets it, so the row is
/// written once.
/// </para>
/// </summary>
internal sealed class DeferredJobLog
{
    private JobLog? _log;

    public void Set(JobLog log) => Volatile.Write(ref _log, log);

    public JobLog? Take() => Interlocked.Exchange(ref _log, null);
}
