namespace Warp.Core.Metrics;

/// <summary>
/// Presence marker registered by <c>AddMetrics</c>, so the dashboard can light its Metrics flag — mirrors
/// <c>IClientObservabilityMarker</c>.
/// </summary>
public interface IMetricsMarker;

public sealed class MetricsMarker : IMetricsMarker;
