using Shouldly;
using Warp.Core.Handlers;
using Warp.Core.Retry;

namespace Warp.Tests.Features.Retry;

// Direct unit coverage for the publish-time half of the retry contract (#236). The behavior must
// freeze a job-type [Retry] attribute into metadata WITHOUT ever materializing the global default —
// materializing the default is exactly what used to shadow a handler-declared [Retry] at execution
// (metadata ?? attribute ?? options never reached the attribute) and what left plain jobs with a
// MaxRetries key that consumers indexed blindly. These assert the dictionary the pipeline hands on.
[Trait("Category", "NoDb")]
public class RetryPublishBehaviorTests
{
    private static readonly PublishDelegate NoOp = () => Task.CompletedTask;

    [TimedFact]
    public async Task PublishAsync_PlainJobWithoutRetryAttribute_StampsNothing()
    {
        var context = new PublishContext<PlainJob> { Job = new PlainJob() };

        await new RetryPublishBehavior<PlainJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        // The regression: a plain job must not carry a retry policy — the default resolves at
        // execution via IOptions, so blindly indexing metadata["MaxRetries"] would throw.
        context.Metadata.ContainsKey("MaxRetries").ShouldBeFalse();
        context.Metadata.ContainsKey("RetryDelays").ShouldBeFalse();
    }

    [TimedFact]
    public async Task PublishAsync_JobWithRetryAttribute_StampsMaxRetriesOnly()
    {
        var context = new PublishContext<AttributeJob> { Job = new AttributeJob() };

        await new RetryPublishBehavior<AttributeJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        context.GetMetadata<IRetryMetadata>().MaxRetries.ShouldBe(5);
        context.Metadata.ContainsKey("RetryDelays").ShouldBeFalse();
    }

    [TimedFact]
    public async Task PublishAsync_JobWithRetryAttributeAndDelays_StampsBoth()
    {
        var context = new PublishContext<AttributeWithDelaysJob> { Job = new AttributeWithDelaysJob() };

        await new RetryPublishBehavior<AttributeWithDelaysJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        var meta = context.GetMetadata<IRetryMetadata>();
        meta.MaxRetries.ShouldBe(3);
        meta.RetryDelays.ShouldBe([7, 9]);
    }

    [TimedFact]
    public async Task PublishAsync_ExplicitMaxRetriesInMetadata_NotOverwrittenByAttribute()
    {
        // A per-enqueue WithRetry(1) must win over the type's [Retry(5)] — the publish behavior fills
        // only when unset (??=), it never clobbers an explicit caller override.
        var context = new PublishContext<AttributeJob> { Job = new AttributeJob() };
        context.GetMetadata<IRetryMetadata>().MaxRetries = 1;

        await new RetryPublishBehavior<AttributeJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        context.GetMetadata<IRetryMetadata>().MaxRetries.ShouldBe(1);
    }

    [TimedFact]
    public async Task PublishAsync_ExplicitDelaysInMetadata_NotOverwrittenByAttribute()
    {
        // Delays are guarded independently of MaxRetries: an explicit WithRetry delay survives, while
        // the still-unset MaxRetries is filled from the attribute.
        var context = new PublishContext<AttributeWithDelaysJob> { Job = new AttributeWithDelaysJob() };
        context.GetMetadata<IRetryMetadata>().RetryDelays = [2];

        await new RetryPublishBehavior<AttributeWithDelaysJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        var meta = context.GetMetadata<IRetryMetadata>();
        meta.RetryDelays.ShouldBe([2]);
        meta.MaxRetries.ShouldBe(3);
    }

    [TimedFact]
    public async Task PublishAsync_ZeroMaxRetriesAttribute_StampsZeroNotUnset()
    {
        // [Retry(0)] is an explicit "no retries", distinct from an absent policy: it must land as 0 in
        // metadata so execution resolves 0 rather than falling through to the global default.
        var context = new PublishContext<ZeroRetryJob> { Job = new ZeroRetryJob() };

        await new RetryPublishBehavior<ZeroRetryJob>().PublishAsync(context, NoOp, Xunit.TestContext.Current.CancellationToken);

        context.GetMetadata<IRetryMetadata>().MaxRetries.ShouldBe(0);
    }

    [TimedFact]
    public async Task PublishAsync_AlwaysInvokesNext()
    {
        var called = false;
        var context = new PublishContext<PlainJob> { Job = new PlainJob() };

        await new RetryPublishBehavior<PlainJob>().PublishAsync(
            context,
            () =>
            {
                called = true;

                return Task.CompletedTask;
            },
            Xunit.TestContext.Current.CancellationToken);

        called.ShouldBeTrue();
    }

    private sealed class PlainJob : IJob;

    [Retry(5)]
    private sealed class AttributeJob : IJob;

    [Retry(3, Delays = [7, 9])]
    private sealed class AttributeWithDelaysJob : IJob;

    [Retry(0)]
    private sealed class ZeroRetryJob : IJob;
}
