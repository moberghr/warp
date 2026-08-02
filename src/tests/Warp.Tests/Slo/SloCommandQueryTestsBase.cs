using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
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
}
