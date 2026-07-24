using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One lifecycle event per Warp process instance — the unified application/instance log covering
/// BOTH server and non-server instances (lifecycle is an application concern, not a server one).
/// Distinct from <c>ServerLog</c>, which stays server-TASK execution history. <see cref="InstanceId"/>
/// is a SOFT reference (no FK, like <c>JobLog</c>): it points at a <c>Server.Id</c> for server
/// instances or an <c>ApplicationInstance.Id</c> for non-server ones. Diagnostic + retention-bounded
/// (age + count, swept by <c>ExpirationCleanup</c>); §1.2 — messages must not carry payloads.
/// </summary>
public class ApplicationInstanceLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid InstanceId { get; set; }

    public required string ApplicationName { get; set; }

    public DateTime Timestamp { get; set; }

    public ApplicationInstanceEventType EventType { get; set; }

    public string? Message { get; set; }

    public DateTime? ExpireAt { get; set; }
}
