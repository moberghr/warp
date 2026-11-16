namespace Warp.Core.Services;

/// <summary>
/// An <see cref="IMetricQueryService"/> query the service refuses to run: an empty or inverted range, a range over
/// <see cref="IMetricQueryService.MaxRangeDays"/> days, more than <see cref="IMetricQueryService.MaxFilters"/> filters,
/// or a malformed filter. A caller's error (an HTTP 400), never a server fault. Derives from
/// <see cref="ArgumentException"/>, so callers that already catch that keep working.
/// </summary>
public sealed class MetricQueryValidationException : ArgumentException
{
    public MetricQueryValidationException()
    {
    }

    public MetricQueryValidationException(string message)
        : base(message)
    {
    }

    public MetricQueryValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MetricQueryValidationException(string message, string paramName)
        : base(message, paramName)
    {
    }

    public MetricQueryValidationException(string message, string paramName, Exception innerException)
        : base(message, paramName, innerException)
    {
    }
}
