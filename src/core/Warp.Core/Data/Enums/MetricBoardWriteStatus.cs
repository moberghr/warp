namespace Warp.Core.Enums;

/// <summary>The outcome of creating or renaming a named metric board. Values start at 1 (§8.11).</summary>
public enum MetricBoardWriteStatus
{
    /// <summary>The board was created or updated.</summary>
    Saved = 1,

    /// <summary>The edited board no longer exists (deleted meanwhile): an edit never re-creates it.</summary>
    NotFound = 2,

    /// <summary>Another board already has the name, ignoring case.</summary>
    NameTaken = 3,

    /// <summary>An insert would take the host past <c>MetricBoardValidation.MaxBoards</c>.</summary>
    Full = 4,
}
