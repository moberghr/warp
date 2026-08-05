using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Refit;
using Shouldly;
using Warp.Core.Logging;
using Warp.Core.Metrics;
using Warp.Metrics.Prometheus;

namespace Warp.Tests.Metrics;

/// <summary>
/// A real OTel → Prometheus → read-back round trip for <see cref="PrometheusMetricSource"/> (§8.33). Pushes the
/// actual Warp adapter meters through an OTLP exporter into a live Prometheus (Testcontainers, OTLP receiver on),
/// then reads them back through the seam and asserts the numbers — proving the full export/query path, not just the
/// generated PromQL. A unique adapter name isolates the assertions from any concurrent meter emissions.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PrometheusRoundTripTests : IAsyncLifetime
{
    // Prometheus 3.x with the OTLP receiver on, and the standard suffixing translation so an OTel counter lands as
    // {name}_total and the ms-unit histogram as {name}_milliseconds_{bucket,sum,count} — the names PrometheusMetricSource expects.
    private const string PrometheusYml =
        "global:\n  scrape_interval: 2s\notlp:\n  translation_strategy: UnderscoreEscapingWithSuffixes\n  keep_identifying_resource_attributes: true\nstorage:\n  tsdb:\n    out_of_order_time_window: 30m\n";

    private const string Adapter = "rt-roundtrip-adapter"; // unique, so parallel tests can't pollute the assertion

    private IContainer _prometheus = null!;
    private string _baseUrl = null!;

    public async ValueTask InitializeAsync()
    {
        _prometheus = new ContainerBuilder()
            .WithImage("prom/prometheus:v3.1.0")
            .WithResourceMapping(Encoding.UTF8.GetBytes(PrometheusYml), "/etc/prometheus/prometheus.yml")
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-otlp-receiver")
            .WithPortBinding(9090, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready")))
            .Build();

        await _prometheus.StartAsync();
        _baseUrl = $"http://{_prometheus.Hostname}:{_prometheus.GetMappedPublicPort(9090)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_prometheus is not null)
        {
            await _prometheus.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExportViaOtel_ReadBackThroughSeam_MatchesRecordedMetrics()
    {
        // 1) Export the real Warp adapter meters to the container's OTLP receiver, with the histogram Views applied.
        using (var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(WarpTelemetry.ServiceName)
            .AddView("warp.adapter.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000],
            })
            .AddOtlpExporter((exporter, reader) =>
            {
                // OTel .NET uses a programmatically-set Endpoint verbatim (no signal-path appending), so give the
                // full Prometheus OTLP metrics path.
                exporter.Endpoint = new Uri($"{_baseUrl}/api/v1/otlp/v1/metrics");
                exporter.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 500;
            })
            .Build())
        {
            for (var i = 0; i < 9; i++)
            {
                RecordCall("success");
            }

            for (var i = 0; i < 2; i++)
            {
                RecordCall("failed");
            }

            // 100 latency samples all at 40 ms → all land in the (25, 50] bucket, so histogram_quantile(0.95)
            // interpolates to ~48.75, deterministically inside (25, 50].
            for (var i = 0; i < 100; i++)
            {
                WarpTelemetry.AdapterDuration.Record(40, Tags("success"));
            }

            provider!.ForceFlush(5000);
        }

        // 2) Read back through the seam, polling until Prometheus has ingested the counter.
        var api = RestService.For<IPrometheusQueryApi>(_baseUrl);
        var source = new PrometheusMetricSource(api, new PrometheusMetricSourceOptions { BaseAddress = _baseUrl });
        var callsRef = new MetricRef(WarpMetricCatalog.Names.AdapterCalls, Tag(WarpMetricCatalog.Tags.Adapter, Adapter));

        var ct = Xunit.TestContext.Current.CancellationToken;

        IReadOnlyList<BreakdownRow> byOutcome = [];
        await PollUntil(async () =>
        {
            byOutcome = await source.GetBreakdownAsync(callsRef, [WarpMetricCatalog.Tags.Outcome], null, ct);
            return byOutcome.Sum(r => r.Value) >= 11;
        });

        // Counts round-tripped: 9 success + 2 failed.
        byOutcome.Single(r => Is(r, "success")).Value.ShouldBe(9);
        byOutcome.Single(r => Is(r, "failed")).Value.ShouldBe(2);

        // Percentile round-tripped through the real histogram export + histogram_quantile.
        var durationRef = new MetricRef(WarpMetricCatalog.Names.AdapterDuration, Tag(WarpMetricCatalog.Tags.Adapter, Adapter));
        var p95 = await source.GetPercentileBreakdownAsync(durationRef, 95, [WarpMetricCatalog.Tags.Adapter], null, ct);
        p95.Single().Value.ShouldBeInRange(25, 50);
    }

    private static bool Is(BreakdownRow row, string outcome)
        => row.Tags.TryGetValue(WarpMetricCatalog.Tags.Outcome, out var v) && string.Equals(v, outcome, StringComparison.Ordinal);

    private static void RecordCall(string outcome) => WarpTelemetry.AdapterCalls.Add(1, Tags(outcome));

    private static TagList Tags(string outcome) => new()
    {
        { WarpTelemetryAttributes.AdapterMeterAdapter, Adapter },
        { WarpTelemetryAttributes.AdapterMeterOperation, "charge" },
        { WarpTelemetryAttributes.AdapterMeterOutcome, outcome },
    };

    private static Dictionary<string, string> Tag(string key, string value)
        => new(StringComparer.Ordinal) { [key] = value };

    // Polls the read-back until the condition holds, since OTLP ingestion + index is not instantaneous. Deterministic
    // (a condition, not a fixed sleep) with a generous ceiling for container/ingestion lag.
    private async Task PollUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (ApiException)
            {
                // Prometheus not ready to answer yet — keep polling.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Prometheus did not ingest the exported metrics within 60s. Ingested metric names: {await DiagnosticNamesAsync()}");
    }

    private async Task<string> DiagnosticNamesAsync()
    {
        using var http = new HttpClient();
        var sb = new StringBuilder();
        try
        {
            sb.Append("names=").Append(await http.GetStringAsync($"{_baseUrl}/api/v1/label/__name__/values"));

            var flags = await http.GetStringAsync($"{_baseUrl}/api/v1/status/flags");
            sb.Append(" | otlp-flag=").Append(flags.Contains("otlp", StringComparison.OrdinalIgnoreCase) ? "present" : "ABSENT");

            using var probe = await http.GetAsync($"{_baseUrl}/api/v1/otlp/v1/metrics");
            sb.Append(" | otlp-endpoint GET status=").Append((int)probe.StatusCode);
        }
        catch (Exception ex)
        {
            sb.Append(" <diagnostic failed: ").Append(ex.Message).Append('>');
        }

        return sb.ToString();
    }
}
