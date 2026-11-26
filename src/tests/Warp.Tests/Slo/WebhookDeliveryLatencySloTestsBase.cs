using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Slo;

/// <summary>
/// The <see cref="SloKind.WebhookDeliveryLatency"/> objective (8.1): the share of a group's deliveries delivered within
/// TargetValue minutes of being sent, read from the delivery rows. Deliveries younger than the threshold and cancelled
/// ones are not counted; exhausted, parked, still pending past the threshold and late ones are bad events.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookDeliveryLatencySloTestsBase : IAsyncLifetime
{
    private const string Group = "acme";

    private readonly IDatabaseFixture _fixture;

    protected WebhookDeliveryLatencySloTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Evaluate_AllDeliveredInTime_IsHealthy()
    {
        var id = await SeedObjectiveAsync(Group, minutes: 5, percentile: 90);
        for (var i = 0; i < 10; i++)
        {
            await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);
        }

        await RunAsync();

        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Healthy);
        eval.Attainment.ShouldBe(1.0);
    }

    [TimedFact]
    public async Task Evaluate_LateExhaustedParkedAndStuckDeliveries_CountAsBad()
    {
        var id = await SeedObjectiveAsync(Group, minutes: 5, percentile: 90);
        for (var i = 0; i < 6; i++)
        {
            await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);
        }

        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 10); // late
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Exhausted, sentMinutesAgo: 30, deliveredAfterMinutes: null);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Parked, sentMinutesAgo: 30, deliveredAfterMinutes: null);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Pending, sentMinutesAgo: 30, deliveredAfterMinutes: null); // stuck past 5 min

        await RunAsync();

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.6, 1e-9);
        eval.State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_CancelledYoungAndOtherGroups_AreNotCounted()
    {
        var id = await SeedObjectiveAsync(Group, minutes: 5, percentile: 90);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Cancelled, sentMinutesAgo: 30, deliveredAfterMinutes: null);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Pending, sentMinutesAgo: 1, deliveredAfterMinutes: null); // may still make it
        await SeedDeliveryAsync("globex", WebhookDeliveryStatus.Exhausted, sentMinutesAgo: 30, deliveredAfterMinutes: null);

        await RunAsync();

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(1.0);
        eval.State.ShouldBe(SloState.Healthy);
    }

    [TimedFact]
    public async Task Evaluate_ThresholdLongerThanTheShortWindow_StillFeedsTheFastBurn()
    {
        // 120-minute threshold, 1-day window: the short window (2 h) ends at the settle point, so a delivery sent 3 h
        // ago and never delivered lands in it and burns fast.
        var id = await SeedObjectiveAsync(Group, minutes: 120, percentile: 90, windowSeconds: 86_400);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Exhausted, sentMinutesAgo: 180, deliveredAfterMinutes: null);

        await RunAsync();

        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Breaching);
        eval.BurnRateShort.ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task Evaluate_DeliveredRowWithoutADeliveryTime_IsNotCounted()
    {
        // A Delivered row from before 8.1 has no LastAttemptAt; it is left out, not counted as late.
        var id = await SeedObjectiveAsync(Group, minutes: 5, percentile: 90);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: null, withoutAttemptTime: true);

        await RunAsync();

        (await EvalAsync(id)).Attainment.ShouldBe(1.0);
    }

    [TimedFact]
    public async Task Evaluate_NoDeliveriesInTheWindow_IsNoData()
    {
        var id = await SeedObjectiveAsync(Group, minutes: 5, percentile: 90);
        await SeedDeliveryAsync("globex", WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);

        await RunAsync();

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
    }

    [TimedFact]
    public async Task Evaluate_StarDimension_CountsEveryGroup()
    {
        var id = await SeedObjectiveAsync("*", minutes: 5, percentile: 40);
        await SeedDeliveryAsync(Group, WebhookDeliveryStatus.Delivered, sentMinutesAgo: 30, deliveredAfterMinutes: 1);
        await SeedDeliveryAsync("globex", WebhookDeliveryStatus.Exhausted, sentMinutesAgo: 30, deliveredAfterMinutes: null);

        await RunAsync();

        (await EvalAsync(id)).Attainment.ShouldBe(0.5, 1e-9);
    }

    private async Task<int> SeedObjectiveAsync(string group, double minutes, int percentile, int windowSeconds = 3600)
    {
        var ctx = _fixture.CreateContext();
        var definition = new SloDefinition
        {
            Name = $"webhooks {group}",
            Kind = SloKind.WebhookDeliveryLatency,
            Dimension = group,
            TargetValue = minutes,
            Percentile = percentile,
            WindowSeconds = windowSeconds,
            Enabled = true,
        };

        ctx.Set<SloDefinition>().Add(definition);
        await ctx.SaveChangesAsync(Ct);

        return definition.Id;
    }

    private async Task SeedDeliveryAsync(string group, WebhookDeliveryStatus status, int sentMinutesAgo, int? deliveredAfterMinutes, bool withoutAttemptTime = false)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-sentMinutesAgo);
        DateTime? lastAttemptAt = deliveredAfterMinutes is { } after ? createdAt.AddMinutes(after) : createdAt;
        if (withoutAttemptTime)
        {
            lastAttemptAt = null;
        }

        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            GroupName = group,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = status,
            AttemptCount = 1,
            CreatedAt = createdAt,
            LastAttemptAt = lastAttemptAt,
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<SloEvaluation> EvalAsync(int id)
        => (await _fixture.CreateContext().Set<SloEvaluation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SloDefinitionId == id, Ct)).ShouldNotBeNull();

    private async Task RunAsync()
    {
        var evaluator = new SloEvaluator<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            TimeProvider.System,
            TestNotifiers.EmptyDispatcher(),
            NullLogger<SloEvaluator<TestContext>>.Instance);

        await evaluator.ExecuteAsync(Ct);
        await evaluator.OnCommittedAsync(Ct);
    }
}
