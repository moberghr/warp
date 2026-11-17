using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.SavedViews;

namespace Warp.Core.Services;

/// <summary>Creates, edits and deletes saved views (§8.34). Registered by <c>AddWarp</c>, so a dashboard-only host resolves it.</summary>
public interface ISavedViewCommandService
{
    /// <summary>
    /// Insert (Id ≤ 0) or update a view as <paramref name="actor"/>, the acting dashboard user. An insert stores the actor as
    /// the author; an update keeps the original author. Only the author may edit a personal view, or change whether a view
    /// is shared (<see cref="SavedViewWriteStatus.Forbidden"/>); anyone may edit a shared view's name, page, target and
    /// query. An edit of a deleted view is <see cref="SavedViewWriteStatus.NotFound"/> and never re-creates it. Inserts and
    /// a switch to shared respect <see cref="SavedViewValidation.MaxViewsPerAuthor"/> and
    /// <see cref="SavedViewValidation.MaxSharedViews"/>. Throws <see cref="TimeoutException"/> when another write held
    /// the saved-view lock too long.
    /// </summary>
    /// <remarks>Persists <paramref name="view"/> as given: callers validate it first with <see cref="SavedViewValidation.TryValidate"/>.</remarks>
    Task<SavedViewWriteResult> Upsert(SavedView view, string actor, CancellationToken ct);

    /// <summary>
    /// Deletes a view as <paramref name="actor"/>: a shared view by anyone, a personal view by its author only. Throws
    /// <see cref="TimeoutException"/> when another write held the saved-view lock too long.
    /// </summary>
    Task<SavedViewDeleteStatus> Delete(int id, string actor, CancellationToken ct);
}

/// <summary>
/// <see cref="ISavedViewCommandService"/> over the user's <typeparamref name="TContext"/>, with a structured
/// <c>LogInformation</c> audit naming the acting user and the page, never the query (§1.2: it holds tag values an operator
/// filtered on). Inserts, edits and deletes run under the transaction-scoped <c>warp:saved-view</c> lock (§2.16), so two
/// saves at once cannot both pass a cap and a delete cannot act on a sharing flag the author is changing at that moment;
/// without a provider they run unguarded.
/// </summary>
public sealed class SavedViewCommandService<TContext> : ISavedViewCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SavedViewCommandService<TContext>> _logger;
    private readonly IWarpSqlQueries<TContext>? _sqlQueries;

    public SavedViewCommandService(TContext context, TimeProvider timeProvider, ILogger<SavedViewCommandService<TContext>> logger, IWarpSqlQueries<TContext>? sqlQueries = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
        _sqlQueries = sqlQueries;
    }

    /// <summary>How long a write waits for the saved-view lock before it gives up with a <see cref="TimeoutException"/>.</summary>
    internal TimeSpan LockTimeout { get; init; } = TransactionLock.CommandTimeout;

    public async Task<SavedViewWriteResult> Upsert(SavedView view, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);

        var user = MetricAnnotationValidation.NormalizeAuthor(actor);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var insert = view.Id <= 0;
        var result = await TransactionLock.RunAsync(
            _sqlQueries,
            _context,
            TransactionLock.SavedViewKey,
            LockTimeout,
            (x, token) => insert ? InsertAsync(x, view, user, now, token) : UpdateAsync(x, view, user, now, token),
            ct);
        if (result.Status == SavedViewWriteStatus.Saved && insert)
        {
            _logger.LogInformation("Saved view {Id} created by {Actor} on {Page}", result.Id, user, view.Page);
        }

        if (result.Status == SavedViewWriteStatus.Saved && !insert)
        {
            _logger.LogInformation("Saved view {Id} updated by {Actor} on {Page}", result.Id, user, view.Page);
        }

        return result;
    }

    public async Task<SavedViewDeleteStatus> Delete(int id, string actor, CancellationToken ct)
    {
        var user = MetricAnnotationValidation.NormalizeAuthor(actor);

        // The sharing check and the delete run under the lock an unshare takes, re-reading the row there: read outside it,
        // a view the author made personal a moment ago could still be deleted by someone else as "shared".
        var (status, page) = await TransactionLock.RunAsync(
            _sqlQueries,
            _context,
            TransactionLock.SavedViewKey,
            LockTimeout,
            (x, token) => DeleteAsync(x, id, user, token),
            ct);
        if (status == SavedViewDeleteStatus.Deleted)
        {
            _logger.LogInformation("Saved view {Id} deleted by {Actor} on {Page}", id, user, page);
        }

        return status;
    }

    private static async Task<(SavedViewDeleteStatus Status, string? Page)> DeleteAsync(DbContext context, int id, string user, CancellationToken ct)
    {
        var view = await context.Set<SavedView>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);
        if (view is null)
        {
            return (SavedViewDeleteStatus.NotFound, null);
        }

        if (!view.Shared && !IsAuthor(view, user))
        {
            return (SavedViewDeleteStatus.Forbidden, view.Page);
        }

        context.Set<SavedView>().Remove(view);
        await context.SaveChangesAsync(ct);

        return (SavedViewDeleteStatus.Deleted, view.Page);
    }

    private static async Task<SavedViewWriteResult> InsertAsync(DbContext context, SavedView view, string user, DateTime now, CancellationToken ct)
    {
        if (await CountByAuthorAsync(context, user, ct) >= SavedViewValidation.MaxViewsPerAuthor)
        {
            return new SavedViewWriteResult { Status = SavedViewWriteStatus.AuthorFull };
        }

        if (view.Shared && await CountSharedAsync(context, ct) >= SavedViewValidation.MaxSharedViews)
        {
            return new SavedViewWriteResult { Status = SavedViewWriteStatus.SharedFull };
        }

        // A fresh entity: never the caller's key (SQL Server refuses an explicit identity value) or a body's author.
        var created = new SavedView
        {
            Name = view.Name,
            Page = view.Page,
            Target = view.Target,
            Query = view.Query,
            Shared = view.Shared,
            Author = user,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Set<SavedView>().Add(created);
        await context.SaveChangesAsync(ct);

        return new SavedViewWriteResult { Status = SavedViewWriteStatus.Saved, Id = created.Id };
    }

    private static async Task<SavedViewWriteResult> UpdateAsync(DbContext context, SavedView view, string user, DateTime now, CancellationToken ct)
    {
        var existing = await context.Set<SavedView>()
            .Where(x => x.Id == view.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            return new SavedViewWriteResult { Status = SavedViewWriteStatus.NotFound };
        }

        // A personal view is its author's alone; on a shared view anyone edits the content, but only the author decides
        // whether it is shared, so nobody can hide another user's view from everyone (or publish their personal one).
        var author = IsAuthor(existing, user);
        if ((!existing.Shared || existing.Shared != view.Shared) && !author)
        {
            return new SavedViewWriteResult { Status = SavedViewWriteStatus.Forbidden };
        }

        if (view.Shared && !existing.Shared && await CountSharedAsync(context, ct) >= SavedViewValidation.MaxSharedViews)
        {
            return new SavedViewWriteResult { Status = SavedViewWriteStatus.SharedFull };
        }

        existing.Name = view.Name;
        existing.Page = view.Page;
        existing.Target = view.Target;
        existing.Query = view.Query;
        existing.Shared = view.Shared;
        existing.UpdatedAt = now;
        await context.SaveChangesAsync(ct);

        return new SavedViewWriteResult { Status = SavedViewWriteStatus.Saved, Id = existing.Id };
    }

    // Ordinal, in memory: a case-insensitive collation must not make "alice" the author of "Alice"'s views.
    private static bool IsAuthor(SavedView view, string user) => string.Equals(view.Author, user, StringComparison.Ordinal);

    private static async Task<int> CountByAuthorAsync(DbContext context, string user, CancellationToken ct)
    {
        var authors = await context.Set<SavedView>()
            .AsNoTracking()
            .Where(x => x.Author == user)
            .Select(x => x.Author)
            .ToListAsync(ct);

        return authors.Count(x => string.Equals(x, user, StringComparison.Ordinal));
    }

    private static Task<int> CountSharedAsync(DbContext context, CancellationToken ct)
        => context.Set<SavedView>()
            .Where(x => x.Shared)
            .CountAsync(ct);
}
