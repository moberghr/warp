using System.Text.Json;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Core.Helper;

internal static class JobHelper
{
    // SqlServerWarpSqlQueries serializes the per-fetch queue list as a \x1F-separated string
    // and reconstructs it via STRING_SPLIT. A queue name containing the separator would be
    // silently split into multiple matches — fail fast at publish time so the bug surfaces near
    // the caller instead of quietly widening their fetch filter in production.
    private const char QueueSeparatorChar = '\x1F';

    private static Job CreateJobInternal(string message, string type, DateTime? scheduleTime, string? queue, Guid? parentId, State? state, DateTime now, string? metadata = null, string? application = null)
    {
        var effectiveQueue = queue ?? "default";
        if (effectiveQueue.Contains(QueueSeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Queue name must not contain the ASCII unit separator (\\x1F) — it's reserved for internal SQL Server queue-list encoding.",
                nameof(queue));
        }

        var effectiveScheduleTime = scheduleTime ?? now;
        var job = new Job
        {
            CreateTime = now,
            Message = message,
            Type = type,
            ScheduleTime = effectiveScheduleTime,
            CurrentState = state ?? DefaultState(parentId, effectiveScheduleTime, now),
            Queue = effectiveQueue,
            ParentJobId = parentId,
            Metadata = metadata,
            Application = application,
        };

        return job;
    }

    public static Job CreateJob<T>(
        T message,
        DateTime? scheduleTime,
        string? queue,
        Guid? parentId,
        State? state,
        DateTime now,
        string? metadata = null,
        string? application = null)
        where T : class, IJob
    {
        var serializedMessage = JsonSerializer.Serialize(message);
        var type = message!.GetType().AssemblyQualifiedName!;

        return CreateJobInternal(serializedMessage, type, scheduleTime, queue, parentId, state, now, metadata, application);
    }

    public static Job CreateJob(string message, string type, DateTime? scheduleTime, string? queue, Guid? parentId, State? state, DateTime now, string? metadata = null, string? application = null)
    {
        return CreateJobInternal(message, type, scheduleTime, queue, parentId, state, now, metadata, application);
    }

    private static State DefaultState(Guid? parentId, DateTime scheduleTime, DateTime now)
    {
        if (parentId != null)
        {
            return State.Awaiting;
        }

        if (scheduleTime > now)
        {
            return State.Scheduled;
        }

        return State.Enqueued;
    }
}
