using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>A saved view as the dashboard lists it (§8.34).</summary>
public sealed class SavedViewModel
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The page id: <c>metric</c>, <c>issues</c>, <c>jobs</c> or <c>jobs-by-type</c>.</summary>
    public string Page { get; init; } = string.Empty;

    /// <summary>The instrument, job state or job type the page's path names; null for <c>issues</c>.</summary>
    public string? Target { get; init; }

    /// <summary>The page's URL query string, without <c>?</c>.</summary>
    public string Query { get; init; } = string.Empty;

    public bool Shared { get; init; }

    public string Author { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// The views a dashboard user can see, plus who the server takes them to be: the Views menu splits "Mine" from "Shared"
/// by <see cref="User"/>, and defaults a new view to shared when it is <c>anonymous</c> (an open dashboard, §1.5).
/// </summary>
public sealed class SavedViewListModel
{
    /// <summary>The acting dashboard user, as the author of a view they save would be stored.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>The caller's own views and every shared view, newest first.</summary>
    public IReadOnlyList<SavedViewModel> Views { get; init; } = [];
}

/// <summary>The outcome of <c>ISavedViewCommandService.Upsert</c>: a status, and the view's id when it was saved.</summary>
public sealed class SavedViewWriteResult
{
    public SavedViewWriteStatus Status { get; init; }

    /// <summary>The created or updated view's id; 0 unless <see cref="Status"/> is <see cref="SavedViewWriteStatus.Saved"/>.</summary>
    public int Id { get; init; }
}
