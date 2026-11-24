using Microsoft.Extensions.Logging;

namespace Warp.Worker;

// The worker's per-job log lines, source-generated: each checks IsEnabled itself and, when the level is
// on, logs without the params array and the boxed ids a LogInformation call allocates for every job.
internal static partial class WorkerLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {workerId} fetched job {id}")]
    public static partial void FetchedJob(ILogger logger, Guid workerId, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {workerId} executing job {id}")]
    public static partial void ExecutingJob(ILogger logger, Guid workerId, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {workerId} completed job {id}")]
    public static partial void CompletedJob(ILogger logger, Guid workerId, Guid id);
}
