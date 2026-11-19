using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>One stored metric annotation (#333): a known-issue note on an instrument or an SLO objective.</summary>
public sealed class MetricAnnotationModel
{
    public int Id { get; init; }

    public MetricAnnotationTarget Target { get; init; }

    public string? Instrument { get; init; }

    public string? TagFilter { get; init; }

    public int? SloDefinitionId { get; init; }

    public string Note { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public DateTime StartsAt { get; init; }

    /// <summary>Null = ongoing.</summary>
    public DateTime? EndsAt { get; init; }

    public bool AcknowledgedNoise { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>A read of annotations: newest start first, at most <c>MetricAnnotationValidation.MaxPerRead</c>.</summary>
public sealed class MetricAnnotationListModel
{
    public IReadOnlyList<MetricAnnotationModel> Items { get; init; } = [];

    /// <summary>True when more annotations matched than the read returns.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>
/// Which annotations to read: at most one target (an instrument, every note on it whatever its filter; or an SLO
/// objective), and optionally only those overlapping <c>[From, To)</c>. An ongoing note overlaps every window from its
/// start on. Nothing set reads every annotation.
/// </summary>
public sealed class MetricAnnotationQuery
{
    public string? Instrument { get; init; }

    public int? SloDefinitionId { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}
