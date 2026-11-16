namespace Warp.Core.Data.Entities;

/// <summary>
/// One detected deploy: the first time an instance of <see cref="Application"/> registered with a
/// <see cref="Version"/> not seen before for that application in that <see cref="Environment"/>. Recorded
/// insert-if-absent at instance registration (server processes in <c>WarpServerRegistration</c>, non-server
/// processes in <c>ApplicationHeartbeatHost</c>) — never on the worker path — and only when both
/// <c>ApplicationName</c> and <c>ApplicationVersion</c> are set. A unique index on
/// (<see cref="Application"/>, <see cref="Environment"/>, <see cref="Version"/>) makes two instances of a new
/// version registering at once produce exactly one row. Deploy history is not tied to instance liveness: the
/// instance sweeps never touch it, and it has its own retention (<c>ApplicationDeployRetention</c>).
/// </summary>
public class ApplicationDeploy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Application { get; set; }

    public required string Version { get; set; }

    /// <summary>
    /// The reporting instance's environment, or <see cref="string.Empty"/> when it reported none. Stored
    /// non-null on purpose: it is part of the unique deploy key, and a NULL in a unique index is distinct from
    /// every other NULL on PostgreSQL while EF's SQL Server convention filters NULLs out of the index — either
    /// way two "no environment" rows for one version would both be accepted.
    /// </summary>
    public string Environment { get; set; } = string.Empty;

    public required DateTime FirstSeenAt { get; set; }

    /// <summary>The first instance seen with this version — a soft reference (no FK) to a <c>Server.Id</c> or an <c>ApplicationInstance.Id</c>, which may since be gone.</summary>
    public required Guid InstanceId { get; set; }

    public string? InstanceName { get; set; }
}
