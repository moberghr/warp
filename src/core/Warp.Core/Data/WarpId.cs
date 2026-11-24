namespace Warp.Core.Data;

/// <summary>
/// Ids for the rows Warp writes in volume: jobs, their log rows and the call logs.
/// <para>
/// Time-ordered (UUID version 7), not random. A random id lands each insert on a random page of the
/// primary-key index, so a busy table touches and rewrites pages all over it; an id that starts with
/// its creation time keeps inserts on the newest pages. They are still plain <see cref="Guid"/>
/// values, unique without coordination, and old and new ids mix freely in one table.
/// </para>
/// <para>
/// The ordering benefit is PostgreSQL's. SQL Server compares a uniqueidentifier by its last bytes
/// first, so there these behave like the random ids they replace, no better and no worse.
/// </para>
/// </summary>
internal static class WarpId
{
    public static Guid Next() => Guid.CreateVersion7();
}
