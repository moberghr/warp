using System.Globalization;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Worker.Logging;

/// <summary>
/// Builds the <see cref="JobLog"/> row(s) that record a job's transition to its final state
/// after worker execution. Shared between <c>WarpWorkerService</c> (individual-worker mode)
/// and <c>WarpDispatcherWorker</c> (dispatcher mode) so the two execution paths never drift
/// on the event-type vocabulary, message formatting, or split-on-retry behavior.
/// </summary>
internal static class FinalizationLogs
{
    /// <summary>
    /// Returns one or two log rows describing the job's transition. A retry-due-to-error
    /// (terminal state is <see cref="State.Enqueued"/> or <see cref="State.Scheduled"/> AND
    /// <paramref name="error"/> is non-null) emits a <c>Failed</c> row carrying the exception
    /// followed by a <c>Scheduled</c>/<c>Enqueued</c> row carrying the next-attempt timestamp.
    /// All other transitions emit a single row whose <c>EventType</c> matches the literal state.
    /// </summary>
    public static IReadOnlyList<JobLog> Build(
        Job job,
        Exception? error,
        double? durationMs,
        Guid workerId,
        DateTime now,
        JobOutcome? outcome)
    {
        var state = job.CurrentState;

        if (error != null && (state == State.Enqueued || state == State.Scheduled))
        {
            var scheduledAt = job.ScheduleTime.ToString("o", CultureInfo.InvariantCulture);
            return
            [
                new JobLog
                {
                    JobId = job.Id,
                    EventType = "Failed",
                    Timestamp = now,
                    Level = "Error",
                    Message = outcome?.LogMessage ?? error.Message,
                    Exception = error.ToString(),
                    DurationMs = durationMs,
                    WorkerId = workerId,
                },
                new JobLog
                {
                    JobId = job.Id,
                    EventType = state == State.Scheduled ? "Scheduled" : "Enqueued",
                    Timestamp = now,
                    Level = "Information",
                    Message = $"Retry scheduled for {scheduledAt}",
                    WorkerId = workerId,
                },
            ];
        }

        var eventType = state switch
        {
            State.Completed => "Completed",
            State.Failed => "Failed",
            State.Enqueued => "Enqueued",
            State.Scheduled => "Scheduled",
            State.Deleted => "Deleted",
            _ => state.ToString(),
        };

        var logMessage = outcome?.LogMessage
            ?? (error != null ? error.Message : $"Job {job.Id} {eventType.ToLowerInvariant()}");

        return
        [
            new JobLog
            {
                JobId = job.Id,
                EventType = eventType,
                Timestamp = now,
                Level = state == State.Failed ? "Error" : "Information",
                Message = logMessage,
                Exception = error?.ToString(),
                DurationMs = durationMs,
                WorkerId = workerId,
            },
        ];
    }
}
