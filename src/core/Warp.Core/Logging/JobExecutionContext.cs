namespace Warp.Core.Logging;

internal class JobExecutionInfo
{
    public Guid JobId { get; set; }

    public Guid TraceId { get; set; }

    /// <summary>The client session id carried down from the publish that spawned this job, so jobs it spawns inherit it (§8.27).</summary>
    public string? Session { get; set; }
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
