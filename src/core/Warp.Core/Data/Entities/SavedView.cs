namespace Warp.Core.Data.Entities;

/// <summary>
/// A saved view (§8.34, saved-views spec Part 2 step B): a dashboard page plus its URL query string, stored under a name so
/// an operator can reopen a carefully set-up chart or list. <see cref="Shared"/> views are listed to everyone; personal
/// views only to their <see cref="Author"/>. Stores only what the operator typed plus the author taken from the
/// authenticated dashboard user (§1.2). Kept forever; nothing sweeps them. Always-in-schema (§2.11), mirrored by
/// <c>WarpServerContext</c> (§2.14).
/// </summary>
public class SavedView
{
    public int Id { get; set; }

    /// <summary>The name shown in the Views menu, at most <c>SavedViewValidation.MaxNameLength</c> characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The page id, from <c>SavedViewValidation.Pages</c>: <c>metric</c>, <c>issues</c>, <c>jobs</c> or <c>jobs-by-type</c>.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>
    /// What the page's path names: the instrument of a <c>metric</c> view, the state of a <c>jobs</c> view, the job type of a
    /// <c>jobs-by-type</c> view; null for <c>issues</c>.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>The page's URL query string without the leading <c>?</c>, holding only keys that page reads.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>True: listed to every dashboard user, who may edit and delete it. False: its author's only.</summary>
    public bool Shared { get; set; }

    /// <summary>The dashboard user who created the view (their identity name), or <c>anonymous</c> on an open dashboard.</summary>
    public string Author { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
