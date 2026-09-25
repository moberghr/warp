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
    public const int Current = 1;
}
