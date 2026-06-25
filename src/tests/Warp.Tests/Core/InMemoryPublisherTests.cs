using Shouldly;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Testing;

namespace Warp.Tests.Core;

/// <summary>
/// Verifies the shipped test publisher records each publish API faithfully so handlers that
/// call <c>IPublisher</c> are testable without a Warp store (no <c>Job</c> DbSet required).
/// </summary>
[Trait("Category", "NoDb")]
public sealed class InMemoryPublisherTests
{
    [TimedFact]
    public async Task Enqueue_RecordsJobPayload()
    {
        var publisher = new InMemoryPublisher();

        var id = await publisher.Enqueue(new SampleJob("work"));

        var published = publisher.Published.ShouldHaveSingleItem();
        published.Id.ShouldBe(id);
        published.Kind.ShouldBe(PublishedJobKind.Job);
        published.Payload.ShouldBeOfType<SampleJob>().Tag.ShouldBe("work");
    }

    [TimedFact]
    public async Task Publish_RecordsMessageKind()
    {
        var publisher = new InMemoryPublisher();

        await publisher.Publish(new SampleMessage("evt"), "notifications");

        var published = publisher.Published.ShouldHaveSingleItem();
        published.Kind.ShouldBe(PublishedJobKind.Message);
        published.Queue.ShouldBe("notifications");
    }

    [TimedFact]
    public async Task Schedule_CapturesScheduleTime()
    {
        var publisher = new InMemoryPublisher();
        var when = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await publisher.Schedule(new SampleJob("later"), when);

        publisher.Published.ShouldHaveSingleItem().ScheduleTime.ShouldBe(when);
    }

    [TimedFact]
    public async Task Enqueue_WithJobParameters_CapturesQueueAndParent()
    {
        var publisher = new InMemoryPublisher();
        var parentId = Guid.NewGuid();

        await publisher.Enqueue(new SampleJob("child"), new JobParameters { Queue = "batch", ParentId = parentId });

        var published = publisher.Published.ShouldHaveSingleItem();
        published.Queue.ShouldBe("batch");
        published.ParentJobId.ShouldBe(parentId);
    }

    [TimedFact]
    public async Task SaveChangesAsync_IncrementsCount()
    {
        var publisher = new InMemoryPublisher();

        await publisher.SaveChangesAsync();
        await publisher.SaveChangesAsync();

        publisher.SaveChangesCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task StartNew_RecordsBatchWithChildren()
    {
        var publisher = new InMemoryPublisher();

        await publisher.StartNew([new SampleJob("a"), new SampleJob("b")], name: "import");

        var batch = publisher.Batches.ShouldHaveSingleItem();
        batch.Kind.ShouldBe(PublishedBatchKind.StartNew);
        batch.Name.ShouldBe("import");
        batch.Children.Count.ShouldBe(2);
    }

    [TimedFact]
    public async Task ContinueBatchWith_RecordsContinuationAndParent()
    {
        var publisher = new InMemoryPublisher();
        var parentId = Guid.NewGuid();

        await publisher.ContinueBatchWith([new SampleJob("c")], parentId);

        var batch = publisher.Batches.ShouldHaveSingleItem();
        batch.Kind.ShouldBe(PublishedBatchKind.Continuation);
        batch.ParentId.ShouldBe(parentId);
    }

    private sealed record SampleJob(string Tag) : IJob;

    private sealed record SampleMessage(string Tag) : IMessage;
}
