using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Slo;

/// <summary>
/// Database coverage for <see cref="SloEvaluator{TContext}"/> (§8.31) — computes attainment/budget/state from the
/// durable metric aggregates and upserts <see cref="SloEvaluation"/>, firing a <c>SloBreached</c> /
/// <c>BacklogBreached</c> operational event on a healthy→breaching edge (post-commit), suppressed while
/// acknowledged. Jobstat counters are seeded at a recent fine (5-min) bucket so they fall in both the objective
/// window and the fast-burn short window.
/// </summary>
[GenerateDatabaseTests]
public abstract class SloEvaluatorTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SloEvaluatorTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Evaluate_HealthySuccessRate_UpsertsHealthy_NoAlert()
    {
        var id = await SeedObjectiveAsync(SloKind.SuccessRate, "MyJob", 0.95);
        await SeedJobstatAsync("MyJob", succeeded: 100, failed: 0);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.ShouldNotBeNull();
        eval.State.ShouldBe(SloState.Healthy);
        eval.BudgetRemaining.ShouldBeGreaterThan(0.9);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_BreachingSuccessRate_FiresSloBreached()
    {
        var id = await SeedObjectiveAsync(SloKind.SuccessRate, "MyJob", 0.99);
        await SeedJobstatAsync("MyJob", succeeded: 90, failed: 10); // 90% vs 99% target → budget blown

        var spy = new SpyNotifier();
        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.ShouldNotBeNull();
        eval.State.ShouldBe(SloState.Breaching);
        eval.BudgetRemaining.ShouldBeLessThan(0);
        spy.Received.ShouldContain(e => e.Type == WarpEventType.SloBreached);
    }

    [TimedFact]
    public async Task Evaluate_BacklogOverTarget_FiresBacklogBreached()
    {
        var id = await SeedObjectiveAsync(SloKind.BacklogDepth, "default", 100);
        await SeedStatisticAsync("qbacklog:default:depth", 250); // > 100

        var spy = new SpyNotifier();
        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.ShouldNotBeNull();
        eval.State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldContain(e => e.Type == WarpEventType.BacklogBreached);
    }

    [TimedFact]
    public async Task Evaluate_AcknowledgedBreach_DoesNotFire()
    {
        var id = await SeedObjectiveAsync(SloKind.SuccessRate, "MyJob", 0.99);
        await SeedJobstatAsync("MyJob", succeeded: 90, failed: 10);

        // Pre-seed an acknowledged (breaching) evaluation so this tick stays Acknowledged, not a fresh edge.
        var ctx = _fixture.CreateContext();
        ctx.Set<SloEvaluation>().Add(new SloEvaluation { SloDefinitionId = id, State = SloState.Breaching, BudgetRemaining = -1, AcknowledgedUntil = DateTime.UtcNow.AddHours(1), LastEvaluatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.ShouldNotBeNull();
        eval.State.ShouldBe(SloState.Acknowledged);
        spy.Received.ShouldBeEmpty();
    }

    private async Task<int> SeedObjectiveAsync(SloKind kind, string dimension, double target)
    {
        var ctx = _fixture.CreateContext();
        var def = new SloDefinition { Name = $"{kind} {dimension}", Kind = kind, Dimension = dimension, TargetValue = target, WindowSeconds = 3600, Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.Set<SloDefinition>().Add(def);
        await ctx.SaveChangesAsync(Ct);

        return def.Id;
    }

    private async Task SeedJobstatAsync(string dimension, long succeeded, long failed)
    {
        var suffix = MetricTiers.Suffix(MetricTier.Fine, DateTime.UtcNow, 5);
        var ctx = _fixture.CreateContext();
        ctx.Set<Counter>().Add(new Counter { Key = JobStatsKeys.History(JobStatsKeys.TypeMarker, dimension, JobStatsKeys.SucceededToken, suffix), Value = (int)succeeded });
        ctx.Set<Counter>().Add(new Counter { Key = JobStatsKeys.History(JobStatsKeys.TypeMarker, dimension, JobStatsKeys.FailedToken, suffix), Value = (int)failed });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedStatisticAsync(string key, long value)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<SloEvaluation?> EvalAsync(int id)
        => await _fixture.CreateContext().Set<SloEvaluation>().AsNoTracking().FirstOrDefaultAsync(x => x.SloDefinitionId == id, Ct);

    private async Task RunAsync(SpyNotifier spy)
    {
        var evaluator = new SloEvaluator<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            TimeProvider.System,
            TestNotifiers.SpyDispatcher(spy),
            NullLogger<SloEvaluator<TestContext>>.Instance);

        await evaluator.ExecuteAsync(Ct);
        await evaluator.OnCommittedAsync(Ct);
    }
}
