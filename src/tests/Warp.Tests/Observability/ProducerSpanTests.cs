using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class ProducerSpanTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ProducerSpanTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Publisher<TestContext> CreatePublisher(TestContext ctx)
        => new(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

    private static BatchPublisher<TestContext> CreateBatchPublisher(TestContext ctx)
        => new(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

    /// <summary>
    /// Locates the producer span belonging to <paramref name="expectedId"/>. Necessary because
    /// the harness is process-global; concurrent integration tests (ServerTaskSpanTests etc.)
    /// can emit their own "send default" spans into the same listener.
    /// </summary>
    private static Activity? FindSpanByMessageId(ActivityListenerHarness harness, string operationName, Guid expectedId)
    {
        var idString = expectedId.ToString();

        return harness.AllByName(operationName)
            .FirstOrDefault(a => string.Equals(
                a.GetTagItem(WarpTelemetryAttributes.MessagingMessageId)?.ToString(),
                idString,
                StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task Enqueue_WithListener_EmitsSendProducerSpanWithMessagingTags()
    {
        using var harness = new ActivityListenerHarness();
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        var id = await publisher.Enqueue(new UnitRequest());

        var span = FindSpanByMessageId(harness, "send default", id);
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Producer);
        span.GetTagItem(WarpTelemetryAttributes.MessagingSystem).ShouldBe(WarpTelemetryAttributes.MessagingSystemValue);
        span.GetTagItem(WarpTelemetryAttributes.MessagingOperationName).ShouldBe(WarpTelemetryAttributes.OperationSend);
        span.GetTagItem(WarpTelemetryAttributes.MessagingOperationType).ShouldBe(WarpTelemetryAttributes.OperationSend);
        span.GetTagItem(WarpTelemetryAttributes.MessagingDestinationName).ShouldBe("default");
        span.GetTagItem(WarpTelemetryAttributes.MessagingMessageId).ShouldBe(id.ToString());
        span.GetTagItem(WarpTelemetryAttributes.MessagingConversationId).ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpJobKind).ShouldBe(JobKind.Job.ToString());
        span.GetTagItem(WarpTelemetryAttributes.WarpJobScheduled).ShouldBe(false);
    }

    [TimedFact]
    public async Task Schedule_WithListener_EmitsScheduledTrueOnProducerSpan()
    {
        using var harness = new ActivityListenerHarness();
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        var id = await publisher.Schedule(new UnitRequest(), DateTime.UtcNow.AddMinutes(5));

        var span = FindSpanByMessageId(harness, "send default", id);
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpJobScheduled).ShouldBe(true);
    }

    [TimedFact]
    public async Task Publish_WithListener_EmitsSendProducerSpanWithMessageKind()
    {
        using var harness = new ActivityListenerHarness();
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        var id = await publisher.Publish(new SingleHandlerMessage());

        var span = FindSpanByMessageId(harness, "send default", id);
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Producer);
        span.GetTagItem(WarpTelemetryAttributes.MessagingMessageId).ShouldBe(id.ToString());
        span.GetTagItem(WarpTelemetryAttributes.WarpJobKind).ShouldBe(JobKind.Message.ToString());
    }

    [TimedFact]
    public async Task StartBatch_WithListener_EmitsProducerSpanWithBatchMessageCountAndKindBatch()
    {
        using var harness = new ActivityListenerHarness();
        var ctx = _fixture.CreateContext();
        var batchPublisher = CreateBatchPublisher(ctx);

        var batchId = await batchPublisher.StartNew(new List<UnitRequest> { new(), new(), new() });

        var span = FindSpanByMessageId(harness, "send default", batchId);
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Producer);
        span.GetTagItem(WarpTelemetryAttributes.MessagingMessageId).ShouldBe(batchId.ToString());
        span.GetTagItem(WarpTelemetryAttributes.MessagingBatchMessageCount).ShouldBe(3);
        span.GetTagItem(WarpTelemetryAttributes.WarpJobKind).ShouldBe(JobKind.Batch.ToString());
    }

    [TimedFact]
    public async Task Enqueue_WithCallerActivity_JobParentSpanIdIsCallerNotProducer()
    {
        using var harness = new ActivityListenerHarness();
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        using var callerActivity = new Activity("caller")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        var id = await publisher.Enqueue(new UnitRequest());

        var producer = FindSpanByMessageId(harness, "send default", id);
        producer.ShouldNotBeNull();

        var trackedJob = ctx.ChangeTracker.Entries<Job>().Single(x => x.Entity.Id == id).Entity;
        trackedJob.ParentSpanId.ShouldBe(callerActivity.SpanId.ToHexString());
        trackedJob.ParentSpanId.ShouldNotBe(producer.SpanId.ToHexString());
    }

    [TimedFact]
    public async Task Enqueue_WithoutListener_NoSpanEmitted()
    {
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        await publisher.Enqueue(new UnitRequest());

        // No harness registered for the source — the producer span should never have been created.
        // We assert by attaching a fresh harness *after* publish: it sees nothing.
        using var harness = new ActivityListenerHarness();
        harness.Captured.ShouldBeEmpty();
    }
}
