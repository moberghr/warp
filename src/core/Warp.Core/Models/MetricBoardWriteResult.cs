using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>The outcome of <c>IMetricBoardCommandService.UpsertBoard</c>: a status, and the board's id when it was saved.</summary>
public sealed class MetricBoardWriteResult
{
    public MetricBoardWriteStatus Status { get; init; }

    /// <summary>The created or updated board's id; 0 unless <see cref="Status"/> is <see cref="MetricBoardWriteStatus.Saved"/>.</summary>
    public int Id { get; init; }
}
