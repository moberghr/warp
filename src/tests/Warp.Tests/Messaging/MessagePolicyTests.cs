using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Messaging;

/// <summary>
/// Addon policy axis on MESSAGES: contract-declared policy is copied to every handler's child job
/// (all handlers contend on the shared key); handler-declared policy applies to that handler's
/// children only. Routed children must keep <c>HandlerType</c> on policy requeues (§8.14) — it IS
/// the routing decision, and re-discovery looks up <c>IJobHandler&lt;T&gt;</c> for a type that only
/// has <c>IMessageHandler&lt;T&gt;</c> registrations.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class MessagePolicyTestsBase : IntegrationTestBase
{
    protected MessagePolicyTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task ContractDeclaredMutex_AllHandlersChildrenShareTheKey()
    {
        // SC11: [Mutex] on the MESSAGE type — the publish pipeline stamps it, MessageRouter copies
        // it to both handlers' children, and they contend on ONE lock: whichever child enters the
        // handler first holds it, the other short-circuits to Deleted (Skip mode).
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg => cfg.Services.AddSingleton(barrier));
        var publisher = server.CreatePublisher();

        var messageId = await publisher.Publish(new ContractMutexMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        await WarpTestServer.WaitUntil(async () =>
        {
            var ctx = Fixture.CreateContext();
            var children = await ctx.Set<Job>()
                .AsNoTracking()
                .Where(x => x.ParentJobId == messageId)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);

            return children.Count == 2 && children.Count(x => x.CurrentState == State.Deleted) == 1;
        });

        var readCtx = Fixture.CreateContext();
        var allChildren = await readCtx.Set<Job>()
            .AsNoTracking()
            .Where(x => x.ParentJobId == messageId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        // Contract-declared policy travels via the router's metadata copy — both children carry the
        // key from creation, before any execution.
        allChildren.ShouldAllBe(x => x.Metadata != null && x.Metadata.Contains("msg-contract-mutex", StringComparison.Ordinal));

        var deleted = allChildren.Single(x => x.CurrentState == State.Deleted);
        var logs = await server.GetJobLogs(deleted.Id);
        logs.ShouldContain(l => l.EventType == "Deleted" && l.Message.Contains("msg-contract-mutex", StringComparison.Ordinal));

        barrier.CanFinish.Release();
        await server.WaitForCompletion();
    }

    [TimedFact]
    public async Task HandlerDeclaredMutex_OnlyThatHandlersChildrenAreSerialized()
    {
        // SC12: [Mutex] on ONE of two handlers. The attributed handler's children serialize across
        // messages; the plain handler's children run unconstrained — the message contract knows
        // nothing about the constraint.
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg => cfg.Services.AddSingleton(barrier));
        var publisher = server.CreatePublisher();

        var message1Id = await publisher.Publish(new HandlerMutexMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The attributed handler's child enters and holds the mutex; the plain child completes.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForChildState(server, message1Id, nameof(HandlerMutexMessagePlainHandler), State.Completed);

        var publisher2 = server.CreatePublisher();
        var message2Id = await publisher2.Publish(new HandlerMutexMessage());
        await publisher2.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Message 2: the plain handler's child is untouched by the held mutex; the attributed
        // handler's child short-circuits to Deleted (Skip mode) while the slot is held.
        await WaitForChildState(server, message2Id, nameof(HandlerMutexMessagePlainHandler), State.Completed);
        var deletedChild = await WaitForChildState(server, message2Id, nameof(HandlerMutexMessageHandlerA), State.Deleted);

        // Handler-resolved policy is stamped into the child's metadata and persisted — the row
        // explains the skip even though neither the message nor the row carried it at creation.
        deletedChild.Metadata.ShouldNotBeNull();
        deletedChild.Metadata.ShouldContain("msg-handler-mutex");

        barrier.CanFinish.Release();
        await server.WaitForCompletion();
    }

    [TimedFact]
    public async Task WaitModeHandlerMutex_RoutedChildKeepsHandlerTypeAndCompletes()
    {
        // SC13 (§8.14 guard): a Wait-mode bounce requeues the routed child. HandlerType must be
        // KEPT — cleared, the next attempt would try IJobHandler<WaitMutexMessage> discovery, find
        // nothing, and hard-fail. Completing after the slot frees proves the full requeue→refetch→
        // re-dispatch cycle survives.
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg => cfg.Services.AddSingleton(barrier));
        var publisher = server.CreatePublisher();

        var message1Id = await publisher.Publish(new WaitMutexMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var publisher2 = server.CreatePublisher();
        var message2Id = await publisher2.Publish(new WaitMutexMessage());
        await publisher2.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait until child 2 has bounced at least once (Enqueued log naming the key), then assert
        // the routing decision is still on the row.
        var child2 = await WaitForChild(message2Id, nameof(WaitMutexMessageHandler));
        await server.WaitForJobLog(child2.Id, "Enqueued");

        var bounced = await server.GetJob(child2.Id);
        bounced.HandlerType.ShouldNotBeNull();

        // Free the slot: child 1 completes, child 2 enters on a later poll and completes.
        barrier.CanFinish.Release();
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        barrier.CanFinish.Release();

        await server.WaitForJobState(child2.Id, State.Completed);
        var child1 = await WaitForChildState(server, message1Id, nameof(WaitMutexMessageHandler), State.Completed);
        child1.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact(15_000)]
    public async Task HandlerDeclaredRetry_MessageChildRetriesPerChild_SiblingUnaffected()
    {
        // SC14: [Retry(2, Delays = [1])] on the failing handler must win over the test server's
        // global default (MaxRetries = 1) — the handler rung — and count per child. The plain
        // sibling handler completes untouched. Budget 15s: two 1s retry delays ride the 250ms
        // scheduled-activation sweep plus routing/poll latency on both providers.
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var messageId = await publisher.Publish(new RetryPolicyMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForChildState(server, messageId, nameof(RetryPolicyMessagePlainHandler), State.Completed);
        var failed = await WaitForChildState(server, messageId, nameof(RetryPolicyMessageFailingHandler), State.Failed);

        // Two retries granted by the HANDLER attribute (global default is 1) — per-child counting.
        failed.Metadata.ShouldNotBeNull();
        failed.Metadata.ShouldContain("\"RetriedTimes\":2");
    }

    private async Task<Job> WaitForChild(Guid messageId, string handlerNameFragment)
    {
        Job? child = null;
        await WarpTestServer.WaitUntil(async () =>
        {
            var ctx = Fixture.CreateContext();
            child = await ctx.Set<Job>()
                .AsNoTracking()
                .Where(x => x.ParentJobId == messageId)
                .Where(x => x.HandlerType != null)
                .Where(x => x.HandlerType!.Contains(handlerNameFragment))
                .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

            return child != null;
        });

        return child!;
    }

    private async Task<Job> WaitForChildState(WarpTestServer server, Guid messageId, string handlerNameFragment, State expected)
    {
        var child = await WaitForChild(messageId, handlerNameFragment);
        await server.WaitForJobState(child.Id, expected);

        return await server.GetJob(child.Id);
    }
}
