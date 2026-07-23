namespace Warp.Core.Models;

/// <summary>
/// One row on the Applications roster (§8.19 multi-app observability): all instances of a single logical
/// application (server ∪ non-server) rolled up into counts, summed live CPU/RAM, and the distinct version
/// and environment spread across its instances.
/// </summary>
public sealed class ApplicationSummaryModel
{
    public string Name { get; init; } = string.Empty;

    public int InstanceCount { get; init; }

    public int LiveInstanceCount { get; init; }

    /// <summary>Sum of <c>CpuUsagePercent</c> across live instances that report it; null when none do.</summary>
    public double? TotalCpuUsagePercent { get; init; }

    /// <summary>Sum of <c>MemoryWorkingSetBytes</c> across live instances that report it; null when none do.</summary>
    public long? TotalMemoryWorkingSetBytes { get; init; }

    /// <summary>Distinct non-null versions reported by this application's instances, ordinally sorted.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];

    /// <summary>Distinct non-null environments reported by this application's instances, ordinally sorted.</summary>
    public IReadOnlyList<string> Environments { get; init; } = [];
}
