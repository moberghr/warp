namespace Warp.Core.Logging;

internal class JobExecutionInfo
{
    public Guid JobId { get; set; }

    public Guid TraceId { get; set; }

    // Set by the worker when the handler threw or was cancelled, before its scope is disposed: what the
    // attempt staged is then discarded on purpose, and the unsaved-staged-jobs warning stays quiet.
    public bool HandlerFailed { get; set; }
}

internal static class JobExecutionContext
{
    private static readonly AsyncLocal<JobExecutionInfo?> _current = new();

    public static JobExecutionInfo? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
