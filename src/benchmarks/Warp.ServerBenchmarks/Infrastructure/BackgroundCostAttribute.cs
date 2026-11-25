namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Marks a scenario whose cases add background work to the same drain, one parameter choosing the work
/// (<see cref="Parameter"/>) and one of its values running the drain without any (<see cref="Without"/>). The
/// comparer then reports each case's background work per second of the drain, against that case: per job, work
/// done every second reads cheaper the faster the drain finishes.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BackgroundCostAttribute : Attribute
{
    public BackgroundCostAttribute(string parameter, string without)
    {
        Parameter = parameter;
        Without = without;
    }

    /// <summary>The parameter that chooses the background work.</summary>
    public string Parameter { get; }

    /// <summary>The parameter's value that runs no background work, as BenchmarkDotNet prints it.</summary>
    public string Without { get; }
}
