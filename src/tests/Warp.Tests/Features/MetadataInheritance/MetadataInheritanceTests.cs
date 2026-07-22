using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.MetadataInheritance;

// A handler that publishes a new job inherits the executing (parent) job's entire metadata
// dictionary via JobExecutionContext (Publisher.RunPublishPipeline). That bag carries
// addon operational policy (rate-limit / concurrency / timeout / retry keys), so a plain
// child is silently governed by constraints it never declared. These tests assert the desired
// behaviour — addon config is per-handler, resolved from the child's own attributes — and are
// expected to FAIL until the inherit path stops copying addon-owned keys.
[GenerateDatabaseTests]
public abstract class MetadataInheritanceTestsBase : IntegrationTestBase
{
    protected MetadataInheritanceTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task RateLimitedParent_SpawnsPlainChild_ChildRunsUnthrottledAndDoesNotInheritKey()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var parentId = await publisher.Enqueue(new RateLimitedParentRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);

        // count: 1 — the parent consumed the only bucket slot during its own execution, so the
        // leaked key deterministically sends the child to Deleted. A child that declared no
        // rate limit of its own must run instead.
        child.CurrentState.ShouldBe(State.Completed);
        ShouldNotHaveMetadataKey(child, "RateLimitKey");
    }

    [TimedFact]
    public async Task MutexParent_SpawnsPlainChild_ChildDoesNotInheritConcurrencyKey()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var parentId = await publisher.Enqueue(new MutexParentRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);

        // The mutex is held only during the parent's handler and released on completion, so the
        // behavioural consequence (a child racing into the critical section before then) is
        // timing-dependent. The leak itself is deterministic: the child inherits the key.
        ShouldNotHaveMetadataKey(child, "ConcurrencyKey");
    }

    [TimedFact]
    public async Task SemaphoreParent_SpawnsPlainChild_ChildDoesNotInheritConcurrencyKey()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var parentId = await publisher.Enqueue(new SemaphoreParentRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);
        ShouldNotHaveMetadataKey(child, "ConcurrencyKey");
    }

    [TimedFact]
    public async Task TimeoutParent_SpawnsPlainChild_ChildDoesNotInheritTotalScopeDeadline()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var parentId = await publisher.Enqueue(new TimeoutParentRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);

        // Total-scope timeout stamps an absolute deadline at publish. Inherited into the child
        // it imposes the parent's already-elapsed budget — a child of a long-running parent can
        // begin life already past its deadline and be cancelled instantly.
        ShouldNotHaveMetadataKey(child, "TimeoutDeadlineUtc");
    }

    [TimedFact]
    public async Task RetriedParent_SpawnsPlainChild_ChildDoesNotInheritRetryCount()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        // The parent fails once (RetriedTimes 0 -> 1) then succeeds on retry and spawns the
        // child, so the parent's live RetriedTimes counter is populated when the child inherits.
        var parentId = await publisher.Enqueue(new RetriedParentRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);

        // The child starts fresh work — it must not begin life having "already retried once".
        // Inheriting RetriedTimes=1 silently robs the child of part of its own retry budget.
        child.CurrentState.ShouldBe(State.Completed);
        ShouldNotHaveMetadataKey(child, "RetriedTimes");
    }

    [TimedFact]
    public async Task ParentWithCustomRetryPolicy_SpawnsPlainChild_ChildResolvesItsOwnRetryPolicy()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        // The parent is given a non-default retry policy at publish. Unlike the other addons,
        // MaxRetries/RetryDelays are inherited on every child already — but the leak is masked
        // whenever the parent uses the global default (the inherited value equals what the child
        // would resolve on its own). A custom parent policy makes the leak observable.
        var parentId = await publisher.Enqueue(new SpawnChildJobRequest(), new JobParameters().WithRetry(maxRetries: 5, delays: [7]));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var (parent, child) = await LoadParentAndChild(parentId);

        parent.CurrentState.ShouldBe(State.Completed);
        child.CurrentState.ShouldBe(State.Completed);

        // The child declared no retry policy, so it must carry none: it neither inherits the parent's
        // WithRetry(5, [7]) (#239) nor has the global default materialized into its metadata (#236 —
        // the default is resolved at execution via IOptions, not stamped at publish). Absence of both
        // keys is what proves the child resolved its own default (MaxRetries=1) rather than the parent's.
        ShouldNotHaveMetadataKey(child, "MaxRetries");
        ShouldNotHaveMetadataKey(child, "RetryDelays");
    }

    private async Task<(Job Parent, Job Child)> LoadParentAndChild(Guid parentId)
    {
        var ctx = Fixture.CreateContext();
        var jobs = await ctx.Set<Job>()
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        // After a reset there are exactly two jobs: the parent and the child it spawned.
        jobs.Count.ShouldBe(2);
        var parent = jobs.Single(x => x.Id == parentId);
        var child = jobs.Single(x => x.Id != parentId);

        return (parent, child);
    }

    private static void ShouldNotHaveMetadataKey(Job child, string key)
    {
        if (child.Metadata is null)
        {
            return;
        }

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(child.Metadata)!;
        metadata.ShouldNotContainKey(key);
    }
}
