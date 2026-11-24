namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Marks a benchmark that runs no jobs (an idle server): <see cref="DatabaseStatementsDiagnoser"/> reports its
/// counters per second of the window instead of per job. Every other benchmark is per job, and one whose window
/// wrote no job rows reports no measurement rather than switching unit.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class MeasuredPerSecondAttribute : Attribute;
