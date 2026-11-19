namespace Warp.Core.Enums;

/// <summary>What a metric annotation (#333) is attached to. Values start at 1 (§8.11).</summary>
public enum MetricAnnotationTarget
{
    /// <summary>A registered instrument, optionally narrowed by a <c>MetricFilter</c> tag filter.</summary>
    Instrument = 1,

    /// <summary>An SLO objective (<c>SloDefinition</c>).</summary>
    Slo = 2,
}
