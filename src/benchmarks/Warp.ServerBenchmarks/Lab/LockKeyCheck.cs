using Medallion.Threading;
using Medallion.Threading.Postgres;
using Npgsql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Confirms that the numeric advisory-lock key can be derived WITHOUT the lock library issuing the
/// command, by reading back what Postgres actually locked.
/// <para>
/// This is the compatibility gate on replacing the library's lock commands with unprepared ones of our
/// own. Advisory locks are identified by a 64-bit number, and the library hashes the string name to
/// get it. If a replacement derived that number differently, a rolling deploy would put old and new
/// nodes on DIFFERENT locks for the same logical key — mutual exclusion silently absent for the whole
/// deploy window, so concurrency limits would over-admit and per-key saga serialization would break.
/// That is a correctness break, not a performance one.
/// </para>
/// <para>
/// <see cref="PostgresAdvisoryLockKey"/> is a pure value type: it derives the number with no
/// connection and no prepared statement. If its <c>ToString()</c> round-trips to the same number
/// Postgres reports in <c>pg_locks</c>, the derivation can be reused as-is and the hazard disappears.
/// </para>
/// </summary>
public static class LockKeyCheck
{
    public static async Task RunAsync(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("keycheck requires --connection=<npgsql connection string>.");

            return;
        }

        // Warp's real shapes, all longer than the 9 characters the library maps without hashing.
        string[] names =
        [
            "warp:concurrency:tenant-42",
            "warp:recurring:nightly-report",
            "warp:saga:Acme.Orders.OrderSaga:order-1",
            "warp:ratelimit:outbound-email",
        ];

        Console.WriteLine($"{"lock name",-42}{"derived",20}{"matching locks",20}{"match",8}");

        foreach (var name in names)
        {
            var derived = Derive(name);

            // Through the interface: its CreateLock takes the string name, which is the path Warp uses
            // and therefore the derivation whose output has to be matched.
            IDistributedLockProvider provider = new PostgresDistributedSynchronizationProvider(connectionString);
            await using var handle = await provider.CreateLock(name).TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
            if (handle == null)
            {
                Console.WriteLine($"{name,-42}{"acquire failed — cannot compare",48}");

                continue;
            }

            var held = await CountHeldAdvisoryKeyAsync(connectionString, derived);

            // Asserts the derived key is PRESENT rather than reading back "whatever advisory lock
            // exists". A bare read picks an arbitrary row, and this tool is meant to be run beside a
            // live Postgres (Lab/README.md) where a Warp server holds session locks of exactly this
            // shape — so it would print NO for a derivation that is in fact identical, killing a valid
            // change, or yes by coincidence, which is worse.
            Console.WriteLine($"{name,-42}{derived,20}{held,20}{(held == 1 ? "yes" : "NO"),8}");
        }
    }

    /// <summary>
    /// The number the library's own key type encodes, obtained through its round-trippable string form
    /// rather than by reimplementing its hash.
    /// </summary>
    private static long Derive(string name)
    {
        var text = new PostgresAdvisoryLockKey(name, allowHashing: true).ToString();

        // Documented forms: "{16-digit hex}" for a single 64-bit key, or "{8-hex},{8-hex}" for the
        // two-32-bit-key space. Warp's names always hash to the former, but handle both so a failure
        // here is a clear message rather than a parse exception.
        if (text.Contains(',', StringComparison.Ordinal))
        {
            var parts = text.Split(',');
            var high = (long)uint.Parse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            var low = (long)uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

            return (high << 32) | low;
        }

        return unchecked((long)ulong.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Counts granted advisory locks whose reconstructed 64-bit key equals <paramref name="derived"/>.
    /// Postgres splits that key across <c>classid</c> (high 32 bits) and <c>objid</c> (low 32 bits),
    /// with <c>objsubid = 1</c> marking the single-key space.
    /// <para>
    /// Exactly one is the pass condition. Searching for the derived key rather than reading an
    /// arbitrary advisory row is what makes this usable against a database that other Warp processes
    /// are also locking against.
    /// </para>
    /// <para>
    /// Note <c>objsubid = 1</c>: a name of nine or fewer ASCII characters maps to the TWO-int key space
    /// (<c>objsubid = 2</c>) instead, so adding a short name to the list above would read zero here and
    /// look like a mismatch. Warp's own lock names are all longer than that.
    /// </para>
    /// </summary>
    private static async Task<int> CountHeldAdvisoryKeyAsync(string connectionString, long derived)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM pg_locks "
            + "WHERE locktype = 'advisory' AND granted AND objsubid = 1 "
            + "AND ((classid::bigint << 32) | objid::bigint) = @derived";
        command.Parameters.AddWithValue("derived", derived);

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
