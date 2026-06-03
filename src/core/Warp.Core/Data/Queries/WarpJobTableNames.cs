using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Core.Data.Queries;

/// <summary>
/// Snapshot of the resolved table/column names for <see cref="Job"/> (and <see cref="Server"/>
/// for <c>ServerCleanup</c>) as seen through the user's configured EF Core naming
/// conventions (e.g. <c>UseSnakeCaseNamingConvention</c>) and schema override. Read once at
/// DI setup and interpolated into the cached SQL strings so per-fetch calls pay zero
/// string-formatting cost.
/// </summary>
public sealed class WarpJobTableNames
{
    public string? Schema { get; init; }

    public required string Table { get; init; }

    public string? ServerSchema { get; init; }

    public required string ServerTable { get; init; }

    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Type { get; init; }

    public required string Message { get; init; }

    public required string CreateTime { get; init; }

    public required string ScheduleTime { get; init; }

    public required string CurrentState { get; init; }

    public required string RetriedTimes { get; init; }

    public required string MaxRetries { get; init; }

    public required string Queue { get; init; }

    public required string ParentJobId { get; init; }

    public required string CurrentWorkerId { get; init; }

    public required string HandlerType { get; init; }

    public required string ExpireAt { get; init; }

    public required string LastKeepAlive { get; init; }

    public required string TraceId { get; init; }

    public required string SpawnedByJobId { get; init; }

    public required string JobCount { get; init; }

    public required string ContinuationOptions { get; init; }

    public required string CancellationMode { get; init; }

    public required string Metadata { get; init; }

    public required string ParentSpanId { get; init; }

    public required string ServerId { get; init; }

    public required string ServerLastHeartbeatTime { get; init; }

    public required string ServerMemoryWorkingSetBytes { get; init; }

    public required string ServerCpuUsagePercent { get; init; }

    public required string ServerPausedAt { get; init; }

    public string? WorkerGroupSchema { get; init; }

    public required string WorkerGroupTable { get; init; }

    public required string WorkerGroupId { get; init; }

    public required string WorkerGroupServerId { get; init; }

    public required string WorkerGroupPausedAt { get; init; }

    /// <summary>Schema for the BG-service entities (always present per §2.13).</summary>
    public string? BackgroundServiceSchema { get; init; }

    /// <summary>Table name for <c>BackgroundServiceDefinition</c>.</summary>
    public required string BackgroundServiceDefinitionTable { get; init; }

    /// <summary><c>name</c> column (primary key) on <c>BackgroundServiceDefinition</c>.</summary>
    public string? BackgroundServiceDefinitionName { get; init; }

    /// <summary>Table name for <c>BackgroundServiceInstance</c>.</summary>
    public required string BackgroundServiceInstanceTable { get; init; }

    /// <summary><c>server_id</c> column on <c>BackgroundServiceInstance</c>.</summary>
    public string? BackgroundServiceInstanceServerId { get; init; }

    /// <summary><c>last_heartbeat_at</c> column on <c>BackgroundServiceInstance</c>.</summary>
    public string? BackgroundServiceInstanceLastHeartbeatAt { get; init; }

    /// <summary>Table name for <c>BackgroundServiceLease</c>.</summary>
    public required string BackgroundServiceLeaseTable { get; init; }

    /// <summary><c>holder_server_id</c> column on <c>BackgroundServiceLease</c>.</summary>
    public string? BackgroundServiceLeaseHolderServerId { get; init; }

    /// <summary><c>lease_expires_at</c> column on <c>BackgroundServiceLease</c>.</summary>
    public string? BackgroundServiceLeaseExpiresAt { get; init; }

    /// <summary><c>service_name</c> column on <c>BackgroundServiceLease</c>.</summary>
    public string? BackgroundServiceLeaseServiceName { get; init; }

    /// <summary>
    /// Lease TTL in seconds to use when renewing <c>BackgroundServiceLease</c> rows during
    /// the heartbeat round-trip. Defaults to 30 (the spec default). Set by the provider
    /// factory from <c>WarpWorkerConfiguration.BackgroundServiceLeaseTtl</c>.
    /// </summary>
    public int LeaseTtlSeconds { get; init; } = 30;

    public static WarpJobTableNames FromModel(IModel model, int leaseTtlSeconds = 30)
    {
        var entity = model.FindEntityType(typeof(Job))
            ?? throw new InvalidOperationException("Job entity not registered in the DbContext model.");

        var serverEntity = model.FindEntityType(typeof(Server))
            ?? throw new InvalidOperationException("Server entity not registered in the DbContext model.");

        var groupEntity = model.FindEntityType(typeof(WorkerGroup))
            ?? throw new InvalidOperationException("WorkerGroup entity not registered in the DbContext model.");

        string Col(string propertyName)
        {
            var prop = entity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"Job.{propertyName} property not found in model.");

            return prop.GetColumnName()
                ?? throw new InvalidOperationException($"Job.{propertyName} has no resolved column name.");
        }

        string ServerCol(string propertyName)
        {
            var prop = serverEntity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"Server.{propertyName} property not found in model.");

            return prop.GetColumnName()
                ?? throw new InvalidOperationException($"Server.{propertyName} has no resolved column name.");
        }

        string GroupCol(string propertyName)
        {
            var prop = groupEntity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"WorkerGroup.{propertyName} property not found in model.");

            return prop.GetColumnName()
                ?? throw new InvalidOperationException($"WorkerGroup.{propertyName} has no resolved column name.");
        }

        var definitionEntity = model.FindEntityType(typeof(BackgroundServiceDefinition))
            ?? throw new InvalidOperationException("BackgroundServiceDefinition entity not found in model.");
        var instanceEntity = model.FindEntityType(typeof(BackgroundServiceInstance))
            ?? throw new InvalidOperationException("BackgroundServiceInstance entity not found in model.");
        var leaseEntity = model.FindEntityType(typeof(BackgroundServiceLease))
            ?? throw new InvalidOperationException("BackgroundServiceLease entity not found in model.");

        string? DefinitionCol(string propertyName)
        {
            return definitionEntity.FindProperty(propertyName)?.GetColumnName();
        }

        string? InstanceCol(string propertyName)
        {
            return instanceEntity.FindProperty(propertyName)?.GetColumnName();
        }

        string? LeaseCol(string propertyName)
        {
            return leaseEntity.FindProperty(propertyName)?.GetColumnName();
        }

        return new WarpJobTableNames
        {
            Schema = entity.GetSchema(),
            Table = entity.GetTableName()
                ?? throw new InvalidOperationException("Job entity has no resolved table name."),
            ServerSchema = serverEntity.GetSchema(),
            ServerTable = serverEntity.GetTableName()
                ?? throw new InvalidOperationException("Server entity has no resolved table name."),
            ServerId = ServerCol(nameof(Server.Id)),
            ServerLastHeartbeatTime = ServerCol(nameof(Server.LastHeartbeatTime)),
            ServerMemoryWorkingSetBytes = ServerCol(nameof(Server.MemoryWorkingSetBytes)),
            ServerCpuUsagePercent = ServerCol(nameof(Server.CpuUsagePercent)),
            ServerPausedAt = ServerCol(nameof(Server.PausedAt)),
            WorkerGroupSchema = groupEntity.GetSchema(),
            WorkerGroupTable = groupEntity.GetTableName()
                ?? throw new InvalidOperationException("WorkerGroup entity has no resolved table name."),
            WorkerGroupId = GroupCol(nameof(WorkerGroup.Id)),
            WorkerGroupServerId = GroupCol(nameof(WorkerGroup.ServerId)),
            WorkerGroupPausedAt = GroupCol(nameof(WorkerGroup.PausedAt)),
            Id = Col(nameof(Job.Id)),
            Kind = Col(nameof(Job.Kind)),
            Type = Col(nameof(Job.Type)),
            Message = Col(nameof(Job.Message)),
            CreateTime = Col(nameof(Job.CreateTime)),
            ScheduleTime = Col(nameof(Job.ScheduleTime)),
            CurrentState = Col(nameof(Job.CurrentState)),
            RetriedTimes = Col(nameof(Job.RetriedTimes)),
            MaxRetries = Col(nameof(Job.MaxRetries)),
            Queue = Col(nameof(Job.Queue)),
            ParentJobId = Col(nameof(Job.ParentJobId)),
            CurrentWorkerId = Col(nameof(Job.CurrentWorkerId)),
            HandlerType = Col(nameof(Job.HandlerType)),
            ExpireAt = Col(nameof(Job.ExpireAt)),
            LastKeepAlive = Col(nameof(Job.LastKeepAlive)),
            TraceId = Col(nameof(Job.TraceId)),
            SpawnedByJobId = Col(nameof(Job.SpawnedByJobId)),
            JobCount = Col(nameof(Job.JobCount)),
            ContinuationOptions = Col(nameof(Job.ContinuationOptions)),
            CancellationMode = Col(nameof(Job.CancellationMode)),
            Metadata = Col(nameof(Job.Metadata)),
            ParentSpanId = Col(nameof(Job.ParentSpanId)),
            BackgroundServiceSchema = instanceEntity.GetSchema() ?? definitionEntity.GetSchema(),
            BackgroundServiceDefinitionTable = definitionEntity.GetTableName()
                ?? throw new InvalidOperationException("BackgroundServiceDefinition entity has no resolved table name."),
            BackgroundServiceDefinitionName = DefinitionCol(nameof(BackgroundServiceDefinition.Name)),
            BackgroundServiceInstanceTable = instanceEntity.GetTableName()
                ?? throw new InvalidOperationException("BackgroundServiceInstance entity has no resolved table name."),
            BackgroundServiceInstanceServerId = InstanceCol(nameof(BackgroundServiceInstance.ServerId)),
            BackgroundServiceInstanceLastHeartbeatAt = InstanceCol(nameof(BackgroundServiceInstance.LastHeartbeatAt)),
            BackgroundServiceLeaseTable = leaseEntity.GetTableName()
                ?? throw new InvalidOperationException("BackgroundServiceLease entity has no resolved table name."),
            BackgroundServiceLeaseHolderServerId = LeaseCol(nameof(BackgroundServiceLease.HolderServerId)),
            BackgroundServiceLeaseExpiresAt = LeaseCol(nameof(BackgroundServiceLease.LeaseExpiresAt)),
            BackgroundServiceLeaseServiceName = LeaseCol(nameof(BackgroundServiceLease.ServiceName)),
            LeaseTtlSeconds = leaseTtlSeconds,
        };
    }
}
