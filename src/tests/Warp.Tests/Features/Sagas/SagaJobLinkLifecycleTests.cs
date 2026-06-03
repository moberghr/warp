using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Features.Sagas;

/// <summary>
/// DB-backed tests for the SagaJobLink cleanup contract. Both code paths that remove links
/// (proxy's RemoveLinksForSagaAsync on completion + SagaCommandService.ForceComplete) must
/// leave zero orphan rows, and the deletes must ride the same SaveChanges as the saga removal
/// so a mid-operation failure rolls everything back together.
/// </summary>
[GenerateDatabaseTests]
public abstract class SagaJobLinkLifecycleTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SagaJobLinkLifecycleTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ForceComplete_DeletesSagaAndAllLinks_AtomicTransaction()
    {
        var sagaId = Guid.NewGuid();
        await SeedSagaWithLinks(sagaId, "force-test", linkCount: 5);

        var command = new SagaCommandService<TestContext>(_fixture.CreateContext(), new FakeLockProvider(), NullLogger<SagaCommandService<TestContext>>.Instance);
        var removed = await command.ForceComplete(sagaId);

        removed.ShouldBeTrue();

        // No saga row, no link rows. Cascade FK + RemoveRange in one SaveChanges leaves nothing behind.
        var sagaCount = await _fixture.CreateContext().Set<SagaState>().Where(s => s.Id == sagaId).CountAsync(TestCancellation);
        var linkCount = await _fixture.CreateContext().Set<SagaJobLink>().Where(l => l.SagaId == sagaId).CountAsync(TestCancellation);
        sagaCount.ShouldBe(0);
        linkCount.ShouldBe(0);

        // Operator-action audit signal — both the cumulative and the hour-bucket Counter rows
        // commit in the same SaveChanges as the deletion. Dashboard's Counters page picks them
        // up after CounterAggregator runs.
        var counters = await _fixture.CreateContext().Set<Warp.Core.Data.Entities.Counter>()
            .Where(c => c.Key == "stats:saga_force_completed" || c.Key.StartsWith("stats:saga_force_completed:"))
            .ToListAsync(TestCancellation);
        counters.Sum(c => c.Value).ShouldBe(2); // base + hour bucket = 1 + 1
        counters.Count.ShouldBe(2);
    }

    [TimedFact]
    public async Task ForceComplete_UnknownSagaId_ReturnsFalse_DoesNotTouchOtherSagas()
    {
        var otherSagaId = Guid.NewGuid();
        await SeedSagaWithLinks(otherSagaId, "keep-me", linkCount: 3);

        var command = new SagaCommandService<TestContext>(_fixture.CreateContext(), new FakeLockProvider(), NullLogger<SagaCommandService<TestContext>>.Instance);
        var removed = await command.ForceComplete(Guid.NewGuid()); // unknown

        removed.ShouldBeFalse();

        // Other saga + its links must be untouched.
        var sagaCount = await _fixture.CreateContext().Set<SagaState>().Where(s => s.Id == otherSagaId).CountAsync(TestCancellation);
        var linkCount = await _fixture.CreateContext().Set<SagaJobLink>().Where(l => l.SagaId == otherSagaId).CountAsync(TestCancellation);
        sagaCount.ShouldBe(1);
        linkCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task ForceComplete_MutexHeldByHandler_AbortsWithoutDeleting()
    {
        // Operator clicks force-complete while another worker is mid-HandleAsync — the proxy
        // holds the saga mutex. ForceComplete should refuse rather than racing the handler.
        var sagaId = Guid.NewGuid();
        await SeedSagaWithLinks(sagaId, "mutex-held", linkCount: 2);

        var locks = new FakeLockProvider();
        await using var holder = locks.HoldLock("warp:saga:Test.LifecycleSaga:mutex-held");

        var command = new SagaCommandService<TestContext>(_fixture.CreateContext(), locks, NullLogger<SagaCommandService<TestContext>>.Instance);
        var removed = await command.ForceComplete(sagaId);

        removed.ShouldBeFalse();

        // Saga and its links must still exist — the abort must not delete partial state.
        var sagaCount = await _fixture.CreateContext().Set<SagaState>().Where(s => s.Id == sagaId).CountAsync(TestCancellation);
        var linkCount = await _fixture.CreateContext().Set<SagaJobLink>().Where(l => l.SagaId == sagaId).CountAsync(TestCancellation);
        sagaCount.ShouldBe(1);
        linkCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task DirectSagaDelete_CascadesToLinkRows_ViaForeignKey()
    {
        // Belt-and-braces: even if a code path skips RemoveLinksForSagaAsync and deletes the saga
        // directly, the FK cascade must clean up the link rows.
        var sagaId = Guid.NewGuid();
        await SeedSagaWithLinks(sagaId, "fk-cascade", linkCount: 4);

        var ctx = _fixture.CreateContext();
        var saga = await ctx.Set<SagaState>().FirstAsync(s => s.Id == sagaId, TestCancellation);
        ctx.Set<SagaState>().Remove(saga);
        await ctx.SaveChangesAsync(TestCancellation);

        var linkCount = await _fixture.CreateContext().Set<SagaJobLink>().Where(l => l.SagaId == sagaId).CountAsync(TestCancellation);
        linkCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task GetSagaActivity_OverCap_TruncatesToMostRecent200_FlagsIsTruncated()
    {
        // Seed 250 link rows + jobs so we exceed the 200-row cap. Activity should return only
        // the 200 most-recent (by CreatedAt) and IsTruncated should be true.
        const int totalLinks = 250;
        const int cap = 200;

        var sagaId = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = "Test.OverCapSaga",
            CorrelationKey = "over-cap",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < totalLinks; i++)
        {
            var jobId = Guid.NewGuid();
            arrangeCtx.Set<SagaJobLink>().Add(new SagaJobLink { SagaId = sagaId, JobId = jobId, CreatedAt = baseTime.AddMinutes(i) });
            arrangeCtx.Set<Warp.Core.Entities.Job>().Add(new Warp.Core.Entities.Job
            {
                Id = jobId,
                Kind = Warp.Core.Enums.JobKind.Job,
                CurrentState = Warp.Core.Enums.State.Completed,
                CreateTime = baseTime.AddMinutes(i),
                ScheduleTime = baseTime.AddMinutes(i),
                Queue = "default",
                Type = "Test.Msg",
            });
        }

        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var query = new SagaQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var activity = await query.GetSagaActivity(sagaId);

        activity.Entries.Count.ShouldBe(cap);
        activity.TotalInvocations.ShouldBe(totalLinks);
        activity.IsTruncated.ShouldBeTrue();

        // Returned slice must be the most-recent 200 (CreatedAt 50..249), in ascending order.
        var times = activity.Entries.ConvertAll(e => e.CreateTime);
        times[0].ShouldBe(baseTime.AddMinutes(totalLinks - cap));
        times[^1].ShouldBe(baseTime.AddMinutes(totalLinks - 1));
        for (var i = 1; i < times.Count; i++)
        {
            times[i].ShouldBeGreaterThan(times[i - 1]);
        }
    }

    [TimedFact]
    public async Task GetSagaActivity_OrderedByCreatedAtAscending()
    {
        var sagaId = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = "Test.OrderingSaga",
            CorrelationKey = "ord",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var jobIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Insert links in shuffled order to verify the query orders by CreatedAt, not insert order.
        var insertOrder = new[] { 3, 0, 4, 1, 2 };
        foreach (var i in insertOrder)
        {
            arrangeCtx.Set<SagaJobLink>().Add(new SagaJobLink
            {
                SagaId = sagaId,
                JobId = jobIds[i],
                CreatedAt = baseTime.AddMinutes(i),
            });

            // Also need Job rows so the join in GetSagaActivity returns something.
            arrangeCtx.Set<Warp.Core.Entities.Job>().Add(new Warp.Core.Entities.Job
            {
                Id = jobIds[i],
                Kind = Warp.Core.Enums.JobKind.Job,
                CurrentState = Warp.Core.Enums.State.Completed,
                CreateTime = baseTime.AddMinutes(i),
                ScheduleTime = baseTime.AddMinutes(i),
                Queue = "default",
                Type = "Test.Msg",
            });
        }

        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var query = new SagaQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var activity = await query.GetSagaActivity(sagaId);

        activity.Entries.Select(e => e.JobId).ShouldBe(jobIds);
        activity.TotalInvocations.ShouldBe(5);
        activity.IsTruncated.ShouldBeFalse();
    }

    private async Task SeedSagaWithLinks(Guid sagaId, string correlationKey, int linkCount)
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = "Test.LifecycleSaga",
            CorrelationKey = correlationKey,
            StateJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        for (var i = 0; i < linkCount; i++)
        {
            ctx.Set<SagaJobLink>().Add(new SagaJobLink
            {
                SagaId = sagaId,
                JobId = Guid.NewGuid(),
                CreatedAt = now.AddSeconds(i),
            });
        }

        await ctx.SaveChangesAsync(TestCancellation);
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;
}
