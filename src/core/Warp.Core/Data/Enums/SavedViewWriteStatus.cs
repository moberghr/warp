namespace Warp.Core.Enums;

/// <summary>The outcome of creating or editing a saved view. Values start at 1 (§8.11).</summary>
public enum SavedViewWriteStatus
{
    /// <summary>The view was created or updated.</summary>
    Saved = 1,

    /// <summary>The edited view no longer exists (deleted meanwhile): an edit never re-creates it.</summary>
    NotFound = 2,

    /// <summary>
    /// The caller may not make this edit: the view is someone else's personal view, or the caller is not the author and
    /// tried to change whether it is shared.
    /// </summary>
    Forbidden = 3,

    /// <summary>The author already has <c>SavedViewValidation.MaxViewsPerAuthor</c> views.</summary>
    AuthorFull = 4,

    /// <summary>The host already has <c>SavedViewValidation.MaxSharedViews</c> shared views.</summary>
    SharedFull = 5,
}
