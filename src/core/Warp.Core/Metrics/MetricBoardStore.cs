using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.Metrics;

/// <summary>
/// The board steps the command service and the seeder share. Every method runs inside the <c>warp:metric-board</c>
/// transaction lock (§2.16), so the check-then-insert cannot race another replica or dashboard user.
/// </summary>
internal static class MetricBoardStore
{
    /// <summary>
    /// The Default board, created (named <see cref="MetricBoardValidation.DefaultBoardName"/>, first in picker order) when
    /// it does not exist yet. Created exactly once because the caller holds the board lock; never deleted afterwards.
    /// </summary>
    public static async Task<MetricBoard> EnsureDefaultAsync(DbContext context, DateTime now, CancellationToken ct)
    {
        var existing = await context.Set<MetricBoard>()
            .Where(x => x.IsDefault)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var board = new MetricBoard
        {
            Name = MetricBoardValidation.DefaultBoardName,
            Position = 0,
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Set<MetricBoard>().Add(board);
        await context.SaveChangesAsync(ct);

        return board;
    }

    /// <summary>Whether two board names are the same board: names are unique ignoring case, compared in memory.</summary>
    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
