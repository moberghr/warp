namespace Warp.Core.Enums;

/// <summary>The outcome of deleting a named metric board. Values start at 1 (§8.11).</summary>
public enum MetricBoardDeleteStatus
{
    /// <summary>The board and its tiles were deleted.</summary>
    Deleted = 1,

    /// <summary>No board has that id.</summary>
    NotFound = 2,

    /// <summary>The Default board, which owns the tiles with no board id, cannot be deleted.</summary>
    DefaultBoard = 3,
}
