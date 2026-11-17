namespace Warp.Core.Data.Entities;

/// <summary>
/// A named metrics health board (§8.34): a set of <see cref="MetricBoardTile"/> rows shown together on Metrics → Board.
/// Boards are always shared. One board is the <see cref="IsDefault"/> board, named <c>Default</c> when created: it is
/// created once (by the board command service or the config seeder, under the <c>warp:metric-board</c> lock), cannot be
/// deleted, and owns every tile whose <see cref="MetricBoardTile.BoardId"/> is null, so tiles stored before named boards
/// existed need no data migration. Always-in-schema (§2.11), mirrored by <c>WarpServerContext</c> (§2.14).
/// </summary>
public class MetricBoard
{
    public int Id { get; set; }

    /// <summary>
    /// The board's name, unique ignoring case (enforced in memory under the board lock, never by a collation-dependent
    /// index). The verdict's <c>board</c> parameter and the seeder match boards by it.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Picker order, ascending (ties by id).</summary>
    public int Position { get; set; }

    /// <summary>The application the picker shows the board scoped to by default; it does not filter the tiles.</summary>
    public string? Application { get; set; }

    /// <summary>
    /// True for the one board that owns the tiles with no board id. Its identity survives a rename, so the
    /// <c>AddMetricBoard(b =&gt; ...)</c> overload keeps seeding the same board.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The dashboard user who created the board; null for a board created by the seeder or on first use.</summary>
    public string? CreatedBy { get; set; }
}
