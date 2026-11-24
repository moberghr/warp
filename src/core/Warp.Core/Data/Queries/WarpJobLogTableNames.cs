using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Entities;

namespace Warp.Core.Data.Queries;

/// <summary>
/// The resolved table and column names of <see cref="JobLog"/>, as the host's naming conventions and
/// schema left them. For the one statement that writes job log rows outside EF: the worker's batched
/// completion (<see cref="IWarpSqlQueries{TContext}.CompleteJobsAsync"/>).
/// </summary>
public sealed class WarpJobLogTableNames
{
    public string? Schema { get; init; }

    public required string Table { get; init; }

    public required string Id { get; init; }

    public required string JobId { get; init; }

    public required string EventType { get; init; }

    public required string Timestamp { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public required string Exception { get; init; }

    public required string DurationMs { get; init; }

    public required string WorkerId { get; init; }

    public required string Name { get; init; }

    public required string Value { get; init; }

    public static WarpJobLogTableNames FromModel(IModel model)
    {
        var entity = model.FindEntityType(typeof(JobLog))
            ?? throw new InvalidOperationException("JobLog entity not registered in the DbContext model.");

        string Col(string propertyName)
        {
            var property = entity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"JobLog.{propertyName} property not found in model.");

            return property.GetColumnName()
                ?? throw new InvalidOperationException($"JobLog.{propertyName} has no resolved column name.");
        }

        return new WarpJobLogTableNames
        {
            Schema = entity.GetSchema(),
            Table = entity.GetTableName()
                ?? throw new InvalidOperationException("JobLog entity has no resolved table name."),
            Id = Col(nameof(JobLog.Id)),
            JobId = Col(nameof(JobLog.JobId)),
            EventType = Col(nameof(JobLog.EventType)),
            Timestamp = Col(nameof(JobLog.Timestamp)),
            Level = Col(nameof(JobLog.Level)),
            Message = Col(nameof(JobLog.Message)),
            Exception = Col(nameof(JobLog.Exception)),
            DurationMs = Col(nameof(JobLog.DurationMs)),
            WorkerId = Col(nameof(JobLog.WorkerId)),
            Name = Col(nameof(JobLog.Name)),
            Value = Col(nameof(JobLog.Value)),
        };
    }
}
