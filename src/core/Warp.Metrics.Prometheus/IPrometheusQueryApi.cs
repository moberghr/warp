using Refit;

namespace Warp.Metrics.Prometheus;

/// <summary>
/// The slice of the Prometheus HTTP API (<c>/api/v1</c>) the metric source needs: an instant query (a single
/// evaluation at <c>time</c>) and a range query (evaluations stepped over a window). Warp generates every PromQL
/// expression itself from the logical <c>WarpMetricCatalog</c>; there is no user-authored query.
/// </summary>
public interface IPrometheusQueryApi
{
    [Get("/api/v1/query")]
    Task<PromResponse> QueryAsync([AliasAs("query")] string query, [AliasAs("time")] double? timeUnixSeconds, CancellationToken ct);

    [Get("/api/v1/query_range")]
    Task<PromResponse> QueryRangeAsync(
        [AliasAs("query")] string query,
        [AliasAs("start")] double startUnixSeconds,
        [AliasAs("end")] double endUnixSeconds,
        [AliasAs("step")] string stepDuration,
        CancellationToken ct);
}
