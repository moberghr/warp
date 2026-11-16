namespace Warp.Core.Enums;

/// <summary>
/// How a collected <c>System.Diagnostics.Metrics</c> instrument is aggregated per series and bucket. Values
/// from 1 (§8.11). Observable variants map onto the same kind as their synchronous counterpart: an
/// <c>ObservableCounter</c> is recorded as per-series deltas, so it aggregates exactly like a
/// <c>Counter</c>.
/// </summary>
public enum MetricKind
{
    /// <summary><c>Counter&lt;T&gt;</c> / <c>ObservableCounter&lt;T&gt;</c>: count of measurements and their sum.</summary>
    Counter = 1,

    /// <summary><c>UpDownCounter&lt;T&gt;</c> / <c>ObservableUpDownCounter&lt;T&gt;</c>: the sum of deltas.</summary>
    UpDownCounter = 2,

    /// <summary><c>Histogram&lt;T&gt;</c>: count, sum, min, max and bucket counts.</summary>
    Histogram = 3,

    /// <summary><c>Gauge&lt;T&gt;</c> / <c>ObservableGauge&lt;T&gt;</c>: count, sum, min and max (average = sum / count).</summary>
    Gauge = 4,
}
