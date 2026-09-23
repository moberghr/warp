namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Which database a benchmark runs against.
/// <para>
/// Worth sweeping because nothing else checks the two providers have not drifted. They are kept
/// shape-identical by hand — rule 6.9's scalar claim predicate was applied to both and its record says
/// plainly that the SQL Server plans are "unmeasured" — and every number this project publishes is
/// PostgreSQL's.
/// </para>
/// </summary>
public enum BenchmarkProvider
{
    PostgreSql = 1,
    SqlServer = 2,
}
