namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Marks the statements the HARNESS sends to the database it is measuring (waiting for a drain, cleaning the
/// tables between iterations, making recurring jobs due), so the diagnoser can leave them out of Warp's numbers.
/// <para>
/// They run inside the measured window (BenchmarkDotNet runs <c>IterationCleanup</c> between the window's
/// readings, and a drain is waited for by polling), and were counted as Warp's own: a poll every 100 ms is
/// tens of statements per iteration. Three marks, one per instrument:
/// </para>
/// <list type="bullet">
/// <item><b>PostgreSQL</b>: <see cref="Tag"/> in the statement text (EF <c>TagWith</c>), which
/// <c>pg_stat_statements</c> keeps for a statement shape it has not seen untagged; every harness statement has
/// a shape of its own. The diagnoser drops entries whose text carries it.</item>
/// <item><b>SQL Server</b>: Query Store strips comments from the text it stores (both leading and trailing,
/// checked on 2022), so a tag never reaches it. Harness statements run on a connection with a different
/// language (<see cref="SqlServerLanguage"/>) instead, which Query Store keeps as a separate context setting,
/// and the diagnoser drops queries compiled under it.</item>
/// <item><b>Commands</b> (counted at the driver, <see cref="RoundTripCounter"/>): an async-local flag set by
/// <see cref="Begin"/> moves the harness's commands to a separate count, which the diagnoser also subtracts
/// from PostgreSQL's transactions (each harness statement is a transaction of its own).</item>
/// </list>
/// </summary>
public static class Harness
{
    /// <summary>The marker in a harness statement's text.</summary>
    public const string Tag = "warpbench-harness";

    /// <summary>
    /// The language of the harness's SQL Server connections. Any language but the server default would do;
    /// this one changes nothing a harness statement depends on (it compares and assigns parameters only).
    /// </summary>
    public const string SqlServerLanguage = "British";

    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>Whether the current async flow is sending harness work.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>
    /// Marks everything sent from this async flow as harness work until the scope is disposed. Set from a
    /// synchronous call, so it stays set in the caller's flow (and ends with it).
    /// </summary>
    public static IDisposable Begin()
    {
        Active.Value = true;

        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Active.Value = false;
    }
}
