using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Interceptors;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Metrics;

/// <summary>
/// SC9 read-cost measurement for the metric series read at a mid-size host's scale on PostgreSQL: ten histogram instruments
/// (so the instrument predicate is selective), each 500 series with non-overlapping tiers as rollup leaves them —
/// fine for the last 6h, hourly from there to 7d, daily beyond up to 90d (about 1.58 million rows). For the measured
/// instrument it records four reads — 24h total-only, 7d total-only, 7d breakdown (no percentile) and the 7d
/// percentile + breakdown worst case — each with: whether the plan used the <c>(Instrument, Tier, BucketStart)</c>
/// index, the EXPLAIN (ANALYZE, BUFFERS) execution time of the exact statement EF issues (captured through a command
/// interceptor, parameters included) and the elapsed time of the whole <see cref="IMetricQueryService.GetSeries"/>
/// call, warm and cold. Not a timed assertion and not part of any gate: <b>explicit</b>, PostgreSQL only, run it with
/// <c>--filter-trait "Category=Measurement" --explicit on</c>. The report is written to <c>%TEMP%/mtk-b5-sc9.txt</c>.
/// </summary>
[Xunit.Trait("Category", "Measurement")]
public sealed class MetricQueryScaleMeasurement : IClassFixture<PostgreSqlClassFixture>
{
    private const string Instrument = "scale.latency";
    private const int Instruments = 10;
    private const string Bounds = "5,10,25,50,75,100,250,500,750,1000,2500,5000,7500,10000";
    private const string Buckets = "1,2,3,4,5,6,7,8,9,10,11,12,13,14,15";
    private const int Series = 500;
    private const int FineRows = 72;
    private const int HourlyRows = 162;
    private const int DailyRows = 83;
    private const int BatchSize = 5000;

    private readonly PostgreSqlClassFixture _fixture;

    public MetricQueryScaleMeasurement(PostgreSqlClassFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Xunit.Fact(Explicit = true, Timeout = 600_000)]
    public async Task MeasureSeriesReads_At500SeriesScale_RecordsIndexUseAndElapsedTime()
    {
        var now = DateTime.UtcNow;
        var seedStart = Stopwatch.GetTimestamp();
        var seeded = await SeedAsync(now);
        var seedElapsed = Stopwatch.GetElapsedTime(seedStart);

        await using (var analyze = CreateContext(null))
        {
            await analyze.Database.ExecuteSqlRawAsync("ANALYZE warp.metric_point", Ct);
        }

        var indexName = await FindReadIndexAsync();
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"seed_rows={seeded} instruments={Instruments} seed_seconds={seedElapsed.TotalSeconds:F1} read_index={indexName}");

        var reads = new (string Label, TimeSpan Window, bool Breakdown, int? Percentile)[]
        {
            ("24h_total", TimeSpan.FromHours(24), false, null),
            ("7d_total", TimeSpan.FromDays(7), false, null),
            ("7d_breakdown", TimeSpan.FromDays(7), true, null),
            ("7d_percentile_breakdown", TimeSpan.FromDays(7), true, 95),
        };

        foreach (var (label, window, breakdown, percentile) in reads)
        {
            var query = new MetricSeriesQuery { From = now - window, To = now, Breakdown = breakdown ? "route" : null, Percentile = percentile };

            // Cold read first (plan cache, connection, JIT), then the measured one.
            var cold = Stopwatch.GetTimestamp();
            await CreateService(null).GetSeries(Instrument, query, Ct);
            var coldElapsed = Stopwatch.GetElapsedTime(cold);

            var recorder = new CommandRecorder();
            var measured = Stopwatch.GetTimestamp();
            var result = await CreateService(recorder).GetSeries(Instrument, query, Ct);
            var elapsed = Stopwatch.GetElapsedTime(measured);

            result.ShouldNotBeNull();
            var points = result.Series.Sum(x => x.Points.Count);
            var pointRead = recorder.Commands.Last(x => x.Sql.Contains("metric_point", StringComparison.Ordinal));
            var plan = await ExplainAsync(pointRead.Sql, pointRead.Parameters);

            report.AppendLine(CultureInfo.InvariantCulture, $"--- {label}: display={result.Resolution} lines={result.Series.Count} points={points}");
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}_index_used={indexName is not null && plan.Contains(indexName, StringComparison.Ordinal)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}_get_series_ms_cold={coldElapsed.TotalMilliseconds:F1}");
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}_get_series_ms={elapsed.TotalMilliseconds:F1}");
            report.AppendLine(CultureInfo.InvariantCulture, $"{label}_sql={pointRead.Sql.ReplaceLineEndings(" ")}");
            report.AppendLine(plan);
        }

        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "mtk-b5-sc9.txt"), report.ToString(), Ct);

        seeded.ShouldBe(Instruments * Series * (FineRows + HourlyRows + DailyRows));
    }

    private static IEnumerable<MetricPoint> Rows(DateTime now, string instrument, int series)
    {
        var tags = $"route=r{series:D3}";
        var fine = MetricTiers.BucketStart(MetricTier.Fine, now, 5);
        var hour = MetricTiers.BucketStart(MetricTier.Hourly, now, 5);
        var day = MetricTiers.BucketStart(MetricTier.Daily, now, 5);

        // As rollup leaves them: fine for the last 6h, hourly from there back to 7d, daily beyond.
        for (var i = 0; i < FineRows; i++)
        {
            yield return Row(instrument, tags, MetricTier.Fine, fine.AddMinutes(-5 * i));
        }

        for (var i = 0; i < HourlyRows; i++)
        {
            yield return Row(instrument, tags, MetricTier.Hourly, hour.AddHours(-6 - i));
        }

        for (var i = 0; i < DailyRows; i++)
        {
            yield return Row(instrument, tags, MetricTier.Daily, day.AddDays(-7 - i));
        }
    }

    private static MetricPoint Row(string instrument, string tags, MetricTier tier, DateTime bucket)
        => new()
        {
            Instrument = instrument,
            Application = "scale",
            Tags = tags,
            Tier = tier,
            BucketStart = bucket,
            Count = 120,
            Sum = 6000,
            Min = 1,
            Max = 900,
            Buckets = Buckets,
            Bounds = Bounds,
            Compacted = true,
        };

    private static string InstrumentName(int index) => index == 0 ? Instrument : $"scale.other{index}";

    private async Task<int> SeedAsync(DateTime now)
    {
        await using (var context = CreateContext(null))
        {
            for (var i = 0; i < Instruments; i++)
            {
                context.Set<MetricInstrument>().Add(new MetricInstrument
                {
                    Name = InstrumentName(i),
                    Meter = "scale",
                    Kind = MetricKind.Histogram,
                    Unit = "ms",
                    Bounds = Bounds,
                    TagKeys = "route",
                    FirstSeenAt = now.AddDays(-90),
                    LastSeenAt = now,
                });
            }

            await context.SaveChangesAsync(Ct);
        }

        var seeded = 0;
        var all = Enumerable.Range(0, Instruments)
            .SelectMany(i => Enumerable.Range(0, Series).SelectMany(x => Rows(now, InstrumentName(i), x)));
        foreach (var batch in all.Chunk(BatchSize))
        {
            await using var context = CreateContext(null);
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.Set<MetricPoint>().AddRange(batch);
            await context.SaveChangesAsync(Ct);
            seeded += batch.Length;
        }

        return seeded;
    }

    private async Task<string?> FindReadIndexAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'metric_point'", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            if (reader.GetString(1).Contains("(instrument, tier, bucket_start)", StringComparison.Ordinal))
            {
                return reader.GetString(0);
            }
        }

        return null;
    }

    private async Task<string> ExplainAsync(string sql, NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter.Clone());
        }

        var plan = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }

    private TestContext CreateContext(CommandRecorder? recorder)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SaveChangesConcurrencyTokenInterceptor());
        if (recorder is not null)
        {
            options.AddInterceptors(recorder);
        }

        return new TestContext(options.Options);
    }

    private MetricQueryService<TestContext> CreateService(CommandRecorder? recorder)
        => new(CreateContext(recorder), Options.Create(new WarpConfiguration()));

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, [.. command.Parameters.OfType<NpgsqlParameter>().Select(x => x.Clone())]));

            return ValueTask.FromResult(result);
        }
    }
}
