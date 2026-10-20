namespace Warp.Core.Services;

/// <summary>
/// <c>JobLog.EventType</c> values the read side treats specially. Everything else is a lifecycle event.
/// </summary>
internal static class JobLogEventTypes
{
    /// <summary>
    /// Written by the handler's own logger (the <c>JobLog</c> entity default). Unbounded per job.
    /// </summary>
    public const string Log = "Log";

    /// <summary>
    /// Written by <c>IJobContext.ReportProgress</c>, one row per changed value per bar.
    /// </summary>
    public const string Progress = "Progress";
}
