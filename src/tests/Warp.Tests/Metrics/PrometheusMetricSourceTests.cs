using System.Text.Json;
using Moq;
using Shouldly;
using Warp.Core.Metrics;
using Warp.Metrics.Prometheus;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb tests for <see cref="PrometheusMetricSource"/> — pin the PromQL each seam operation generates for the
/// adapter family and the parsing of the Prometheus JSON envelope, with a mocked <see cref="IPrometheusQueryApi"/>
/// (no live server). This is the query-shape contract the Testcontainer integration test then validates end-to-end.
/// </summary>
[Trait("Category", "NoDb")]
public class PrometheusMetricSourceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly Mock<IPrometheusQueryApi> _api = new(MockBehavior.Strict);
    private string? _lastQuery;

    private PrometheusMetricSource Source() => new(_api.Object, new PrometheusMetricSourceOptions());

    private void OnInstant(string json)
        => _api.Setup(x => x.QueryAsync(It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Callback<string, double?, CancellationToken>((q, _, _) => _lastQuery = q)
            .ReturnsAsync(Parse(json));

    private void OnRange(string json)
        => _api.Setup(x => x.QueryRangeAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, double, double, string, CancellationToken>((q, _, _, _, _) => _lastQuery = q)
            .ReturnsAsync(Parse(json));

    [Fact]
    public async Task GetBreakdown_Calls_GeneratesSumBy_AndParsesVector()
    {
        OnInstant(Vector(("adapter", "stripe", "outcome", "success", "9"), ("adapter", "twilio", "outcome", "success", "5")));

        var rows = await Source().GetBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.AdapterCalls), ["adapter", "outcome"], null, Ct);

        _lastQuery.ShouldBe("sum by (adapter, outcome) (warp_adapter_calls_total)");
        rows.Count.ShouldBe(2);
        rows.Single(r => TagIs(r.Tags, "adapter", "stripe")).Value.ShouldBe(9);
        rows.Single(r => TagIs(r.Tags, "adapter", "twilio")).Value.ShouldBe(5);
    }

    [Fact]
    public async Task GetBreakdown_Calls_WithAdapterFilter_EmitsLabelMatcher()
    {
        OnInstant(Vector(("outcome", "failed", null, null, "2")));

        await Source().GetBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.AdapterCalls, Tags(("adapter", "stripe"))), ["outcome"], null, Ct);

        _lastQuery.ShouldBe("sum by (outcome) (warp_adapter_calls_total{adapter=\"stripe\"})");
    }

    [Fact]
    public async Task GetBreakdown_Duration_UsesSumFamily()
    {
        OnInstant(Vector(("adapter", "stripe", null, null, "450")));

        var rows = await Source().GetBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.AdapterDuration), ["adapter"], null, Ct);

        _lastQuery.ShouldBe("sum by (adapter) (warp_adapter_duration_milliseconds_sum)");
        rows.Single().Value.ShouldBe(450);
    }

    [Fact]
    public async Task GetPercentileBreakdown_Duration_UsesHistogramQuantile()
    {
        OnInstant(Vector(("adapter", "stripe", null, null, "250")));

        var rows = await Source().GetPercentileBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.AdapterDuration), 95, ["adapter"], null, Ct);

        _lastQuery.ShouldBe("histogram_quantile(0.95, sum by (adapter, le) (warp_adapter_duration_milliseconds_bucket))");
        rows.Single().Value.ShouldBe(250);
    }

    [Fact]
    public async Task GetTagValues_Calls_UsesCountBy_AndReturnsDistinctSorted()
    {
        OnInstant(Vector(("adapter", "twilio", null, null, "1"), ("adapter", "stripe", null, null, "1")));

        var values = await Source().GetTagValuesAsync(new MetricRef(WarpMetricCatalog.Names.AdapterCalls), "adapter", null, Ct);

        _lastQuery.ShouldBe("count by (adapter) (warp_adapter_calls_total)");
        values.ShouldBe(["stripe", "twilio"]);
    }

    [Fact]
    public async Task GetSeries_Calls_BreakdownByOutcome_UsesRangeIncrease_AndParsesMatrix()
    {
        OnRange(Matrix("success", (1_700_000_000, "5"), (1_700_003_600, "3")));

        var series = await Source().GetSeriesAsync(
            new SeriesQuery(new MetricRef(WarpMetricCatalog.Names.AdapterCalls), new MetricWindow(DateTime.MinValue, DateTime.MaxValue), MetricResolution.Hourly, MetricAggregation.Sum, BreakdownBy: "outcome"),
            Ct);

        _lastQuery.ShouldBe("sum by (outcome) (increase(warp_adapter_calls_total[1h]))");
        series.Count.ShouldBe(2);
        series.ShouldAllBe(b => b.TagValue == "success");
        series[0].Value.ShouldBe(5);
        series[1].Value.ShouldBe(3);
    }

    [Fact]
    public async Task GetTotal_Endpoint_SumsCounterInstant()
    {
        OnInstant(Scalar("42"));

        var total = await Source().GetTotalAsync(new MetricRef(WarpMetricCatalog.Names.EndpointCalls), null, Ct);

        _lastQuery.ShouldBe("sum(warp_endpoint_calls_total)");
        total.ShouldBe(42);
    }

    [Fact]
    public async Task GetBreakdown_Endpoint_ByRoute()
    {
        OnInstant(Vector(("route", "GET /orders/{id}", null, null, "12")));

        var rows = await Source().GetBreakdownAsync(new MetricRef(WarpMetricCatalog.Names.EndpointCalls), ["route"], null, Ct);

        _lastQuery.ShouldBe("sum by (route) (warp_endpoint_calls_total)");
        rows.Single(r => TagIs(r.Tags, "route", "GET /orders/{id}")).Value.ShouldBe(12);
    }

    [Fact]
    public async Task GetBreakdown_JobExecution_ByType_MapsLogicalTypeToJobTypeLabel()
    {
        // The job-execution instrument exports job.type → label job_type; the seam speaks the logical tag 'type'.
        // The 'by' clause must group on job_type, and each returned row must be keyed back to the LOGICAL 'type'.
        OnInstant(Vector(("job_type", "SendEmail", "outcome", "succeeded", "7")));

        var rows = await Source().GetBreakdownAsync(new MetricRef(WarpMetricCatalog.Names.JobExecution), ["type", "outcome"], null, Ct);

        _lastQuery.ShouldBe("sum by (job_type, outcome) (warp_job_execution_total)");
        rows.Single().Tags["type"].ShouldBe("SendEmail");
        rows.Single().Tags["outcome"].ShouldBe("succeeded");
        rows.Single().Value.ShouldBe(7);
    }

    [Fact]
    public async Task GetBreakdown_JobExecution_WithTypeFilter_EmitsJobTypeMatcher()
    {
        OnInstant(Vector(("outcome", "failed", null, null, "3")));

        await Source().GetBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.JobExecution, Tags(("type", "SendEmail"))), ["outcome"], null, Ct);

        _lastQuery.ShouldBe("sum by (outcome) (warp_job_execution_total{job_type=\"SendEmail\"})");
    }

    [Fact]
    public async Task GetTagValues_JobExecution_Type_CountsByJobTypeLabel_ReturnsLogicalValues()
    {
        OnInstant(Vector(("job_type", "SendEmail", null, null, "1"), ("job_type", "Resize", null, null, "1")));

        var values = await Source().GetTagValuesAsync(new MetricRef(WarpMetricCatalog.Names.JobExecution), "type", null, Ct);

        _lastQuery.ShouldBe("count by (job_type) (warp_job_execution_total)");
        values.ShouldBe(["Resize", "SendEmail"]);
    }

    [Fact]
    public async Task GetPercentileBreakdown_QueueWait_UsesQueueWaitHistogram()
    {
        OnInstant(Vector(("queue", "default", null, null, "250")));

        var rows = await Source().GetPercentileBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.QueueWait), 95, ["queue"], null, Ct);

        _lastQuery.ShouldBe("histogram_quantile(0.95, sum by (queue, le) (warp_job_queue_wait_milliseconds_bucket))");
        rows.Single().Value.ShouldBe(250);
    }

    [Fact]
    public async Task GetBreakdown_Deadline_ByTypeAndQueue_MapsTypeToJobType()
    {
        OnInstant(Vector(("job_type", "SendEmail", "queue", "default", "9")));

        await Source().GetBreakdownAsync(new MetricRef(WarpMetricCatalog.Names.Deadline), ["type", "queue"], null, Ct);

        _lastQuery.ShouldBe("sum by (job_type, queue) (warp_job_deadline_total)");
    }

    [Fact]
    public async Task GetGauge_QueueDepth_UsesInstantSelectorWithQueueMatcher()
    {
        OnInstant(Scalar("250"));

        var depth = await Source().GetGaugeAsync(new MetricRef(WarpMetricCatalog.Names.QueueDepth, Tags(("queue", "default"))), Ct);

        _lastQuery.ShouldBe("warp_job_queue_depth{queue=\"default\"}");
        depth.ShouldBe(250);
    }

    [Fact]
    public async Task GetPercentileBreakdown_ClientVitals_UsesUnitlessVitalsHistogram()
    {
        // warp.client.vitals is declared with no unit, so its Prometheus base name carries no unit suffix.
        OnInstant(Vector(("vital", "LCP", null, null, "1800")));

        var rows = await Source().GetPercentileBreakdownAsync(
            new MetricRef(WarpMetricCatalog.Names.ClientVitalsValue), 75, ["vital"], null, Ct);

        _lastQuery.ShouldBe("histogram_quantile(0.75, sum by (vital, le) (warp_client_vitals_bucket))");
        rows.Single().Value.ShouldBe(1800);
    }

    [Fact]
    public async Task GetBreakdown_ErrorGroup_ByFingerprint()
    {
        OnInstant(Vector(("fingerprint", "a1b2c3", null, null, "4")));

        await Source().GetBreakdownAsync(new MetricRef(WarpMetricCatalog.Names.ErrorGroupOccurrences), ["fingerprint"], null, Ct);

        _lastQuery.ShouldBe("sum by (fingerprint) (warp_errorgroup_occurrences_total)");
    }

    [Theory]
    [InlineData(WarpMetricCatalog.Names.LifecycleSucceeded)]
    [InlineData(WarpMetricCatalog.Names.LifecycleFailed)]
    [InlineData(WarpMetricCatalog.Names.LifecycleDeleted)]
    [InlineData(WarpMetricCatalog.Names.RecordsDropped)]
    [InlineData(WarpMetricCatalog.Names.SloAttainment)]
    [InlineData(WarpMetricCatalog.Names.SloBudget)]
    public async Task UnservedFamilies_ThrowNotSupported_RatherThanReadingAWrongSeries(string metricName)
    {
        // These families have no clean single-instrument OTel export (§8.33), so the Prometheus backend refuses
        // them loudly instead of silently reading an unrelated or empty series. Pins the deliberate gap so a
        // future "it just returned 0" regression is impossible.
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await Source().GetTotalAsync(new MetricRef(metricName), null, Ct));
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await Source().GetBreakdownAsync(new MetricRef(metricName), [], null, Ct));
    }

    private static bool TagIs(IReadOnlyDictionary<string, string> tags, string key, string value)
        => tags.TryGetValue(key, out var v) && string.Equals(v, value, StringComparison.Ordinal);

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags)
        => tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static PromResponse Parse(string json)
        => JsonSerializer.Deserialize<PromResponse>(json)!;

    // A single-sample instant vector with an empty metric{} — what sum()/an aggregate with no 'by' returns.
    private static string Scalar(string value)
        => $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{{\"metric\":{{}},\"value\":[1700000000,\"{value}\"]}}]}}}}";

    // Builds an instant (vector) response envelope; each tuple is (k1, v1, k2, v2, value) with the second pair optional.
    private static string Vector(params (string K1, string V1, string? K2, string? V2, string Value)[] results)
    {
        var items = results.Select(r =>
        {
            var labels = r.K2 is null
                ? $"{{\"{r.K1}\":\"{r.V1}\"}}"
                : $"{{\"{r.K1}\":\"{r.V1}\",\"{r.K2}\":\"{r.V2}\"}}";

            return $"{{\"metric\":{labels},\"value\":[1700000000,\"{r.Value}\"]}}";
        });

        return $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{string.Join(",", items)}]}}}}";
    }

    private static string Matrix(string outcome, params (long Ts, string Value)[] points)
    {
        var values = string.Join(",", points.Select(p => $"[{p.Ts},\"{p.Value}\"]"));

        return $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"matrix\",\"result\":[{{\"metric\":{{\"outcome\":\"{outcome}\"}},\"values\":[{values}]}}]}}}}";
    }
}
