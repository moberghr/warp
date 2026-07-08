namespace Warp.Core.Logging;

internal class JobExecutionInfo
{
    public Guid JobId { get; set; }

    public Guid TraceId { get; set; }
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
