namespace Warp.Core.Enums;

/// <summary>The outcome of deleting a saved view. Values start at 1 (§8.11).</summary>
public enum SavedViewDeleteStatus
{
    /// <summary>The view was deleted.</summary>
    Deleted = 1,

    /// <summary>No view has that id.</summary>
    NotFound = 2,

    /// <summary>The view is someone else's personal view.</summary>
    Forbidden = 3,
}
