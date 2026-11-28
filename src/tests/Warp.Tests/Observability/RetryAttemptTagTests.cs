using System.Diagnostics;
using Shouldly;
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

    // Every test here waits for the consumer spans it asserts on, not for State.Failed (#364): the row can
    // read Failed before the last attempt's span has stopped, so a test that read the spans then raced it.
    // Nor does any test need the whole retry chain (six attempts a second apart took 7 of the 10 s budget
    // on an idle machine): the handlers retry with no delay, and each test waits only for the attempts its
    // assertion is about.
    [TimedFact]
    public async Task GivenAlwaysFailingJob_WhenRetried_ThenConsumerSpansCarryIncrementingAttemptTag()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ImmediateRetryAttributeRequest());
        await publisher.SaveChangesAsync(ct);

        // Attempts run one after another, so the first three spans to stop are attempts 1 to 3.
        var consumerSpans = await WaitForConsumerSpans(harness, jobId, 3, ct);

        consumerSpans
            .OrderBy(x => x.StartTimeUtc)
            .Take(3)
            .Select(x => x.GetTagItem(WarpTelemetryAttributes.WarpJobAttempt))
            .ShouldBe([1L, 2L, 3L]);

        // Every failed/retried consumer span carries error.type from the worker's catch path.
        // (The worker dispatches via reflection so the underlying exception is wrapped — we
        // only assert the tag is set to a non-empty exception-type string.)
        consumerSpans.ShouldAllBe(x => !string.IsNullOrEmpty(x.GetTagItem(WarpTelemetryAttributes.ErrorType) as string));
    }

    [TimedFact]
    public async Task GivenJobWithMaxRetriesMetadata_WhenProcessed_ThenConsumerSpanCarriesMaxAttemptsTag()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        // WithRetry writes MaxRetries into the metadata dictionary (via the IRetryMetadata
        // setter), which is what the worker reads to populate warp.job.max_attempts. The
        // [Retry(N)] attribute alone doesn't go through the metadata dict — it's read from
        // the type at retry-pipeline-behavior time, not at metadata-deserialize time.
        var jobId = await publisher.Enqueue(
            new RetryAttributeHandlerRequest(),
            new Warp.Core.Helper.JobParameters().WithRetry(maxRetries: 2, delays: [0]));
        await publisher.SaveChangesAsync(ct);

        // The first attempt and a retry.
        var consumerSpans = await WaitForConsumerSpans(harness, jobId, 2, ct);

        // max_attempts = MaxRetries + 1 (initial attempt counts). Set on every consumer span
        // for this job because the metadata key is read on each worker invocation.
        consumerSpans.ShouldAllBe(x => Equals(x.GetTagItem(WarpTelemetryAttributes.WarpJobMaxAttempts), 3L));
    }

    [TimedFact]
    public async Task GivenHandlerRetryAttribute_WhenProcessed_ThenEveryConsumerSpanCarriesMaxAttemptsTag()
    {
        // Nothing is stamped at publish any more (§8.8): the budget is resolved by the pipeline during
        // the attempt. Reading the metadata BEFORE the handler ran left the first attempt's span without
        // max_attempts while attempts 2+ carried it — the tag must be read after the pipeline resolved.
        var ct = Xunit.TestContext.Current.CancellationToken;
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ImmediateRetryAttributeRequest());
        await publisher.SaveChangesAsync(ct);

        // The first attempt and a retry.
        var consumerSpans = await WaitForConsumerSpans(harness, jobId, 2, ct);

        // [Retry(5)] on the handler → 6 attempts, on the FIRST span as much as on the retries.
        consumerSpans.ShouldAllBe(x => Equals(x.GetTagItem(WarpTelemetryAttributes.WarpJobMaxAttempts), 6L));
    }

    private static async Task<IReadOnlyList<Activity>> WaitForConsumerSpans(ActivityListenerHarness harness, Guid jobId, int count, CancellationToken ct)
    {
        var id = jobId.ToString();
        bool OfJob(Activity x) => string.Equals(x.GetTagItem(WarpTelemetryAttributes.MessagingMessageId)?.ToString(), id, StringComparison.Ordinal);

        var spans = await harness.WaitForAsync("process default", x => x.Count(OfJob) >= count, ct);

        return [.. spans.Where(OfJob)];
    }
}
