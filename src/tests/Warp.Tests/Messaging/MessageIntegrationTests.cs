using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Messaging;

[GenerateDatabaseTests]
public abstract class MessageIntegrationTestsBase : IntegrationTestBase
{
    protected MessageIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenSingleHandlerMessage_WhenPublished_ThenMessageCompletesAndHandlerExecutes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var messageId = await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // Message should be completed
        var message = await ctx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.CurrentState.ShouldBe(State.Completed);
        message.Kind.ShouldBe(JobKind.Message);

        // One handler job should have been created and completed
        var handlerJobs = await ctx.Set<Job>()
            .Where(j => j.Kind == JobKind.Job && j.ParentJobId == messageId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        handlerJobs.Count.ShouldBe(1);
        handlerJobs[0].CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task GivenMultiHandlerMessage_WhenPublished_ThenBothHandlersExecuteAndMessageCompletes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var messageId = await publisher.Publish(new MultiRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // Message should be completed
        var message = await ctx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.CurrentState.ShouldBe(State.Completed);

        // Two handler jobs (MultiHandlerA + MultiHandlerB) should be created and completed
        var handlerJobs = await ctx.Set<Job>()
            .Where(j => j.Kind == JobKind.Job && j.ParentJobId == messageId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        handlerJobs.Count.ShouldBe(2);
        handlerJobs.ShouldAllBe(j => j.CurrentState == State.Completed);
    }

    [TimedFact]
    public async Task GivenMessageWithQueue_WhenPublished_ThenChildrenInheritQueue()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var messageId = await publisher.Publish(new SingleHandlerMessage(), "a-critical");
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        var message = await ctx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.Queue.ShouldBe("a-critical");

        var handlerJobs = await ctx.Set<Job>()
            .Where(j => j.Kind == JobKind.Job && j.ParentJobId == messageId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        handlerJobs.Count.ShouldBe(1);
        handlerJobs[0].Queue.ShouldBe("a-critical");
    }

    [TimedFact]
    public async Task GivenMultipleMessages_WhenPublished_ThenAllRouteAndComplete()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var messageIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            messageIds.Add(await publisher.Publish(new SingleHandlerMessage()));
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        foreach (var messageId in messageIds)
        {
            var message = await ctx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
            message.CurrentState.ShouldBe(State.Completed);

            var handlerJobs = await ctx.Set<Job>()
                .Where(j => j.Kind == JobKind.Job && j.ParentJobId == messageId)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);
            handlerJobs.Count.ShouldBe(1);
            handlerJobs[0].CurrentState.ShouldBe(State.Completed);
        }
    }

    [TimedFact]
    public async Task GivenSeveralMultiHandlerMessages_WhenProcessed_ThenEachCompletesWithAllItsHandlerJobs()
    {
        // Three MultiRequest messages (two handlers each, both succeed) in one save: every message completes and
        // spawned exactly its own two handler jobs. (No handler fails here: the test data has no message with a
        // throwing handler.)
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        // Publish several multi-handler messages
        var messageIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            messageIds.Add(await publisher.Publish(new MultiRequest()));
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // All messages should be in a terminal state
        var messages = await ctx.Set<Job>()
            .Where(j => messageIds.Contains(j.Id))
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(3);
        messages.ShouldAllBe(m => m.CurrentState == State.Completed);

        // Total handler jobs = 3 messages x 2 handlers = 6
        var totalHandlerJobs = await ctx.Set<Job>()
            .CountAsync(j => j.Kind == JobKind.Job && j.ParentJobId != null && messageIds.Contains(j.ParentJobId.Value), Xunit.TestContext.Current.CancellationToken);
        totalHandlerJobs.ShouldBe(6);
    }
}
