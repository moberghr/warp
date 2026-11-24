namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Which way of measuring produced a set of numbers, so results recorded against different releases are
/// only compared when they were measured the same way.
/// <para>
/// Each release's benchmark results are attached to it and kept, and the report charts them release over
/// release. That is only meaningful while the harness measures the same thing: a change to a scenario,
/// the fixture, the diagnoser or what a column counts can move every number with no change to Warp at all
/// (fixing the scheduled-backlog scenario to actually schedule its jobs raised its WAL per job by a
/// fifth). <b>Bump this whenever a change alters what any scenario measures</b>, then re-record the latest
/// release with the Performance workflow's <c>record_release</c> input so the history has a point on the
/// new version. Results on a different version are shown as not comparable, never compared.
/// </para>
/// <para>
/// An explicit number rather than a hash of the harness files, because most harness edits — a comment, a
/// clearer report — change nothing measured, and a hash would reset the history on every one of them.
/// </para>
/// </summary>
public static class MeasurementVersion
{
    // 2: what changed from 1, on both providers, so nothing recorded on 1 is compared with it:
    //  - SQL Server statements and buffers are counted from Query Store instead of the plan cache (which lost
    //    the executions of any plan recompiled inside the window, so SQL Server read low and unstable), without
    //    Query Store's internal queries; SQL Server baselines move up.
    //  - The harness's own statements (waiting for a drain, cleaning between iterations, making recurring jobs
    //    due) are no longer counted as Warp's, on either provider (Harness).
    //  - The benchmark waits at each window signal while the readings are taken (blocking acknowledgements), so
    //    the window no longer catches the start of the memory diagnoser's extra iteration.
    //  - PostgreSQL's job divisor is the job table's insert counter (n_tup_ins), not the rows of INSERT
    //    statements of one text; its DB time includes planning (track_planning).
    //  - The rate-limit scenario's rows are per job and gated (version 1 took them for an idle server's
    //    per-second figures, since it has no JobCount parameter), and PostgreSQL's idle windows include planning
    //    time like every other window.
    //  - Results carry the database versions they were measured on.
    public const int Current = 2;
}
