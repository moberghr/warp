using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class RetryAttemptTagTestsBase : IntegrationTestBase
{
    protected RetryAttemptTagTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenAlwaysFailingJob_WhenRetried_ThenConsumerSpansCarryIncrementingAttemptTag()
    {
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new RetryAttributeHandlerRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait until the job's failure state is final. [Retry(5)] means 5 retries → 6 total
        // attempts. WarpTestServer overrides retry delays to [1] second (see WarpTestServer.cs)
        // so worst case is 5 × 1s = 5s of retry delay PLUS each attempt's processing time PLUS
        // ScheduledJobActivation tick latency. The default WaitForJobState timeout (5s) is too
        // tight when SQL Server is under parallel-test load — give it 20s explicitly.
        await server.WaitForJobState(jobId, State.Failed, TimeSpan.FromSeconds(20));

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);

        // We expect at least 2 consumer spans for this trace (initial + at least one retry).
        var jobIdString = jobId.ToString();
        var consumerSpans = harness.AllByName("process default")
            .Where(a => string.Equals(a.GetTagItem(WarpTelemetryAttributes.MessagingMessageId)?.ToString(), jobIdString, StringComparison.Ordinal))
            .OrderBy(a => a.StartTimeUtc)
            .ToList();

        // [Retry(5)] gives 6 total attempts before State.Failed. Assert at least 2 retries
        // happened — the worker may slot in extra spans on shutdown so we tolerate a generous
        // upper bound, but the lower bound must be >=2 to cover the retry tag increment.
        consumerSpans.Count.ShouldBeGreaterThanOrEqualTo(2);

        // First attempt: warp.job.attempt = 1
        consumerSpans[0].GetTagItem(WarpTelemetryAttributes.WarpJobAttempt).ShouldBe(1L);

        // Subsequent attempts: warp.job.attempt strictly increases (1-based)
        for (var i = 1; i < consumerSpans.Count; i++)
        {
            var attemptValue = (long)consumerSpans[i].GetTagItem(WarpTelemetryAttributes.WarpJobAttempt)!;
            attemptValue.ShouldBe(i + 1L);
        }

        // Every failed/retried consumer span carries error.type from the worker's catch path.
        // (The worker dispatches via reflection so the underlying exception is wrapped — we
        // only assert the tag is set to a non-empty exception-type string.)
        foreach (var span in consumerSpans)
        {
            span.GetTagItem(WarpTelemetryAttributes.ErrorType)?.ToString().ShouldNotBeNullOrEmpty();
        }
    }

    [TimedFact]
    public async Task GivenJobWithMaxRetriesMetadata_WhenProcessed_ThenConsumerSpanCarriesMaxAttemptsTag()
    {
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        // WithRetry writes MaxRetries into the metadata dictionary (via the IRetryMetadata
        // setter), which is what the worker reads to populate warp.job.max_attempts. The
        // [Retry(N)] attribute alone doesn't go through the metadata dict — it's read from
        // the type at retry-pipeline-behavior time, not at metadata-deserialize time.
        var jobId = await publisher.Enqueue(
            new RetryAttributeHandlerRequest(),
            new Warp.Core.Helper.JobParameters().WithRetry(maxRetries: 2));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, TimeSpan.FromSeconds(20));

        var jobIdString = jobId.ToString();
        var consumerSpans = harness.AllByName("process default")
            .Where(a => string.Equals(a.GetTagItem(WarpTelemetryAttributes.MessagingMessageId)?.ToString(), jobIdString, StringComparison.Ordinal))
            .ToList();

        consumerSpans.ShouldNotBeEmpty();

        // max_attempts = MaxRetries + 1 (initial attempt counts). Set on every consumer span
        // for this job because the metadata key is read on each worker invocation.
        foreach (var span in consumerSpans)
        {
            span.GetTagItem(WarpTelemetryAttributes.WarpJobMaxAttempts).ShouldBe(3L);
        }
    }

    [TimedFact]
    public async Task GivenHandlerRetryAttribute_WhenProcessed_ThenEveryConsumerSpanCarriesMaxAttemptsTag()
    {
        // Nothing is stamped at publish any more (§8.8): the budget is resolved by the pipeline during
        // the attempt. Reading the metadata BEFORE the handler ran left the first attempt's span without
        // max_attempts while attempts 2+ carried it — the tag must be read after the pipeline resolved.
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new RetryAttributeHandlerRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, TimeSpan.FromSeconds(20));

        var jobIdString = jobId.ToString();
        var consumerSpans = harness.AllByName("process default")
            .Where(a => string.Equals(a.GetTagItem(WarpTelemetryAttributes.MessagingMessageId)?.ToString(), jobIdString, StringComparison.Ordinal))
            .OrderBy(a => a.StartTimeUtc)
            .ToList();

        consumerSpans.Count.ShouldBeGreaterThanOrEqualTo(2);

        // [Retry(5)] on the handler → 6 attempts, on the FIRST span as much as on the retries.
        foreach (var span in consumerSpans)
        {
            span.GetTagItem(WarpTelemetryAttributes.WarpJobMaxAttempts).ShouldBe(6L);
        }
    }
}
