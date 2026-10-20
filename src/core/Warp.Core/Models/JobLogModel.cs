namespace Warp.Core.Models;

public class JobLogModel
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = "Log";

    public DateTime Timestamp { get; set; }

    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }

    public double? DurationMs { get; set; }

    public Guid? WorkerId { get; set; }

    /// <summary>
    /// The progress bar's name. Set only on <c>Progress</c> rows; null on lifecycle and handler-output rows.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The progress bar's percent. Set only on <c>Progress</c> rows; null on lifecycle and handler-output rows.
    /// </summary>
    public short? Value { get; set; }
}
