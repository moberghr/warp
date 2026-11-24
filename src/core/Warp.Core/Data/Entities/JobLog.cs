namespace Warp.Core.Data.Entities;

public class JobLog
{
    public Guid Id { get; set; } = WarpId.Next();

    public Guid JobId { get; set; }

    public string EventType { get; set; } = "Log";

    public DateTime Timestamp { get; set; }

    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }

    public double? DurationMs { get; set; }

    public Guid? WorkerId { get; set; }

    public string? Name { get; set; }

    public short? Value { get; set; }
}
