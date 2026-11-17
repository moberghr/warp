using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>Reads saved views (§8.34). Registered by <c>AddWarp</c>, so a dashboard-only host resolves it.</summary>
public interface ISavedViewQueryService
{
    /// <summary>
    /// The views <paramref name="user"/> can see — their own views and every shared view — newest first, optionally only
    /// those of <paramref name="page"/> and <paramref name="target"/>. Bounded by the caps: at most
    /// <c>SavedViewValidation.MaxViewsPerAuthor</c> + <c>SavedViewValidation.MaxSharedViews</c> rows.
    /// </summary>
    Task<IReadOnlyList<SavedViewModel>> GetViews(string user, string? page, string? target, CancellationToken ct);
}

/// <summary><see cref="ISavedViewQueryService"/> over the user's <typeparamref name="TContext"/>.</summary>
public sealed class SavedViewQueryService<TContext> : ISavedViewQueryService
    where TContext : DbContext
{
    private readonly TContext _context;

    public SavedViewQueryService(TContext context) => _context = context;

    public async Task<IReadOnlyList<SavedViewModel>> GetViews(string user, string? page, string? target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var query = _context.Set<SavedView>()
            .AsNoTracking()
            .Where(x => x.Shared || x.Author == user);
        if (page is not null)
        {
            query = query.Where(x => x.Page == page);
        }

        if (target is not null)
        {
            query = query.Where(x => x.Target == target);
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x =>
                new SavedViewModel
                {
                    Id = x.Id,
                    Name = x.Name,
                    Page = x.Page,
                    Target = x.Target,
                    Query = x.Query,
                    Shared = x.Shared,
                    Author = x.Author,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt,
                })
            .ToListAsync(ct);

        // A case-insensitive collation (SQL Server) can only widen the SQL match: re-checked ordinally, so "Alice" never
        // sees "alice"'s personal views, and a target differing only by case is another target.
        var visible = rows
            .Where(x => x.Shared || string.Equals(x.Author, user, StringComparison.Ordinal))
            .Where(x => page is null || string.Equals(x.Page, page, StringComparison.Ordinal))
            .Where(x => target is null || string.Equals(x.Target, target, StringComparison.Ordinal));

        return [.. visible];
    }
}
