using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Core.Slo;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Slo;

/// <summary>
/// Database coverage for the SLO read/command services (§8.31): objectives insert/update/delete, acknowledge
/// (which flips a breaching evaluation to Acknowledged), and the query list/detail projecting a definition joined
/// to its 1:1 evaluation. Each test drives one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class SloCommandQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SloCommandQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_Insert_ThenListedNotYetEvaluated()
    {
        var id = await Command().Upsert(Objective("Emails succeed", SloKind.SuccessRate, "SendEmail", 0.99), Ct);

        id.ShouldBeGreaterThan(0);
        var list = await Query().GetObjectives(Ct);
        var row = list.Items.ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.Name.ShouldBe("Emails succeed");
        row.Evaluated.ShouldBeFalse();
        row.State.ShouldBe(SloState.Healthy);
    }

    [TimedFact]
    public async Task Upsert_Update_ChangesFields()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);

        await Command().Upsert(new SloDefinition { Id = id, Name = "Y", Kind = SloKind.SuccessRate, Dimension = "SendEmail", TargetValue = 0.95, WindowSeconds = 7200, Enabled = false }, Ct);

        var o = await Query().GetObjective(id, Ct);
        o.ShouldNotBeNull();
        o.Name.ShouldBe("Y");
        o.TargetValue.ShouldBe(0.95);
        o.WindowSeconds.ShouldBe(7200);
        o.Enabled.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_RemovesObjectiveAndEvaluation()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        await SeedEvaluationAsync(id, SloState.Healthy);

        (await Command().Delete(id, Ct)).ShouldBeTrue();

        (await Query().GetObjective(id, Ct)).ShouldBeNull();
        (await _fixture.CreateContext().Set<SloEvaluation>().AnyAsync(x => x.SloDefinitionId == id, Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Acknowledge_BreachingObjective_BecomesAcknowledged()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        await SeedEvaluationAsync(id, SloState.Breaching);

        var until = DateTime.UtcNow.AddHours(1);
        (await Command().Acknowledge(id, until, Ct)).ShouldBeTrue();

        var o = await Query().GetObjective(id, Ct);
        o.ShouldNotBeNull();
        o.State.ShouldBe(SloState.Acknowledged);
        o.AcknowledgedUntil.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Delete_UnknownId_ReturnsFalse()
        => (await Command().Delete(99999, Ct)).ShouldBeFalse();

    [TimedFact]
    public async Task Upsert_MetricRatio_NewFieldsRoundTripThroughGetObjectiveAndList()
    {
        var def = new SloDefinition { Name = "Payments", Kind = SloKind.MetricRatio, Dimension = "payment.results", TagFilter = "region=eu*", RatioTag = "result", GoodValues = "success,ok*", BadValues = "error", BaselineDays = 7, TargetValue = 0.1, WindowSeconds = 3600, Enabled = true };

        var id = await Command().Upsert(def, Ct);

        var detail = await Query().GetObjective(id, Ct);
        var listed = (await Query().GetObjectives(Ct)).Items.ShouldHaveSingleItem();
        foreach (var o in new[] { detail.ShouldNotBeNull(), listed })
        {
            o.Kind.ShouldBe(SloKind.MetricRatio);
            o.TagFilter.ShouldBe("region=eu*");
            o.RatioTag.ShouldBe("result");
            o.GoodValues.ShouldBe("success,ok*");
            o.BadValues.ShouldBe("error");
            o.BaselineDays.ShouldBe(7);
            o.SecondaryMetric.ShouldBeNull();
            o.SecondaryTagFilter.ShouldBeNull();
        }
    }

    [TimedFact]
    public async Task Upsert_MetricGap_SecondaryFieldsRoundTrip()
    {
        var def = new SloDefinition { Name = "Paid", Kind = SloKind.MetricGap, Dimension = "orders.created", TagFilter = "channel=web", SecondaryMetric = "orders.paid", SecondaryTagFilter = "status=ok", TargetValue = 0, WindowSeconds = 3600, Enabled = true };

        var id = await Command().Upsert(def, Ct);

        var o = (await Query().GetObjective(id, Ct)).ShouldNotBeNull();
        o.TagFilter.ShouldBe("channel=web");
        o.SecondaryMetric.ShouldBe("orders.paid");
        o.SecondaryTagFilter.ShouldBe("status=ok");
        o.BaselineDays.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_UpdateToAnotherKind_PersistsTheClearedFields()
    {
        var id = await Command().Upsert(new SloDefinition { Name = "Payments", Kind = SloKind.MetricRatio, Dimension = "payment.results", RatioTag = "result", GoodValues = "success", BadValues = "error", BaselineDays = 1, TargetValue = 0.1, WindowSeconds = 3600, Enabled = true }, Ct);

        // The endpoint validates before Upsert; validation clears the ratio-only fields for a threshold.
        var update = new SloDefinition { Id = id, Name = "Errors", Kind = SloKind.MetricThreshold, Dimension = "payment.results", TagFilter = "result=error", RatioTag = "result", GoodValues = "success", BadValues = "error", BaselineDays = 1, TargetValue = 5, WindowSeconds = 3600, Enabled = true };
        SloValidation.TryValidate(update, out var error).ShouldBeTrue(error);
        await Command().Upsert(update, Ct);

        var o = (await Query().GetObjective(id, Ct)).ShouldNotBeNull();
        o.Kind.ShouldBe(SloKind.MetricThreshold);
        o.TagFilter.ShouldBe("result=error");
        o.RatioTag.ShouldBeNull();
        o.GoodValues.ShouldBeNull();
        o.BadValues.ShouldBeNull();
        o.BaselineDays.ShouldBeNull();
    }

    [TimedFact]
    public async Task Seeder_TwoMetricObjectivesOnOneInstrumentWithDifferentFilters_BothInsert_AndReseedIsIdempotent()
    {
        static void Configure(SloOptions o) => o
            .AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "http.response.status_code=5*")
            .AddMetricThreshold("4xx", "http.server.request.duration", 100, tagFilter: "http.response.status_code=4*");

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows[0].TagFilter.ShouldBe("http.response.status_code=4*");
        rows[1].TagFilter.ShouldBe("http.response.status_code=5*");
    }

    [TimedFact]
    public async Task Seeder_GapWithGaugeSecondaryInstrument_IsSkipped_AndTheValidSeedStillInserts()
    {
        // The seeder runs the endpoint's validator: a gauge has no event count, as a gap's secondary too.
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        await SeedFromConfigAsync(o => o
            .AddMetricGap("gauge gap", "orders.started", "queue.depth", 5)
            .AddMetricThreshold("orders under 10", "orders.started", 10));

        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("orders under 10");
    }

    [TimedFact]
    public async Task PostSlo_SevenDayBaselineBeyondHourlyRetention_Returns400()
    {
        // R29: 7 days of hourly retention is short of 7 days + the 1-hour window + 1 hour.
        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricRatio, "payment.results", 0.1, "result", "success", "error", baselineDays: 7));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_GaugeInstrument_Returns400()
    {
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricThreshold, "queue.depth", 10));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_GapWithGaugeSecondaryInstrument_Returns400()
    {
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricGap, "orders.started", 5, secondaryMetric: "queue.depth"));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_ValidMetricRatio_PersistsTheNewFields()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricRatio, "payment.results", 0.1, "result", "success", "error", baselineDays: 1, tagFilter: "step=authorize"));

        status.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.TagFilter.ShouldBe("step=authorize");
        row.RatioTag.ShouldBe("result");
        row.GoodValues.ShouldBe("success");
        row.BadValues.ShouldBe("error");
        row.BaselineDays.ShouldBe(1);
    }

    private static SloDefinition Objective(string name, SloKind kind, string dimension, double target)
        => new() { Name = name, Kind = kind, Dimension = dimension, TargetValue = target, WindowSeconds = 3600, Enabled = true };

    private async Task SeedEvaluationAsync(int id, SloState state)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<SloEvaluation>().Add(new SloEvaluation { SloDefinitionId = id, State = state, BudgetRemaining = state == SloState.Breaching ? -0.5 : 1.0, LastEvaluatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);
    }

    private SloCommandService<TestContext> Command()
        => new(_fixture.CreateContext(), TimeProvider.System, NullLogger<SloCommandService<TestContext>>.Instance);

    private SloQueryService<TestContext> Query() => new(_fixture.CreateContext());

    private async Task SeedFromConfigAsync(Action<SloOptions> configure)
    {
        var options = new SloOptions();
        configure(options);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        await using var provider = services.BuildServiceProvider();
        var seeder = new SloSeeder<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            NullLogger<SloSeeder<TestContext>>.Instance);

        await seeder.StartAsync(Ct);
    }

    private async Task<HttpStatusCode> PostObjectiveAsync(object body)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddSingleton(Options.Create(new WarpConfiguration { HourlyStatisticsRetention = TimeSpan.FromDays(7) }));
        builder.Services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        builder.Services.AddScoped<ISloCommandService>(_ => Command());

        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/warp/api/slo", body, Ct);
        await app.StopAsync(CancellationToken.None);

        return response.StatusCode;
    }

    private static object MetricRequest(SloKind kind, string instrument, double target, string? ratioTag = null, string? goodValues = null, string? badValues = null, int? baselineDays = null, string? tagFilter = null, string? secondaryMetric = null)
        => new
        {
            id = 0,
            name = $"{kind} {instrument}",
            kind = (int)kind,
            dimension = instrument,
            application = (string?)null,
            targetValue = target,
            percentile = (int?)null,
            windowSeconds = 3600,
            enabled = true,
            tagFilter,
            secondaryMetric,
            secondaryTagFilter = (string?)null,
            ratioTag,
            goodValues,
            badValues,
            baselineDays,
        };

    private async Task SeedInstrumentAsync(string name, MetricKind kind)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<MetricInstrument>().Add(new MetricInstrument { Name = name, Meter = "Test.Meter", Kind = kind, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);
    }
}
