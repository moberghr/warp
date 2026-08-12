using System.Diagnostics.Metrics;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

/// <summary>
/// Dispatcher-mode guard for the retry status label. <c>WarpDispatcherWorker</c> keeps its own copy of the
/// hot-path completion logic, separate from <c>WarpWorkerService</c> — §8.29 records a regression where only
/// one of the two was updated. Both computed <c>willRetry</c> from <see cref="State.Enqueued"/> alone, but
/// <c>JobOutcome.RescheduledState</c> returns <see cref="State.Scheduled"/> for any future retry time and the
/// production default delay is non-zero, so every real retry was emitted as <c>status=failed</c>.
/// <para>
/// The test server retries once with a 1s delay, which lands in <see cref="State.Scheduled"/> — exactly the
/// path that was mislabelled. Asserting both counters (one retried, one failed) pins the full chain: the
/// intermediate attempt is a retry, only the terminal outcome is a failure.
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class RetryStatusDispatcherIntegrationTestsBase : IntegrationTestBase
{
    // MeterListener is process-global and ThrowExceptionRequest is used by several other test classes, so
    // filtering on job type alone lets a concurrently-running test's failures land in these counts. A queue
    // unique to this instance is the isolation key every other metrics test in this namespace uses.
    private readonly string _queue = $"metrics-dispatch-retry-{Guid.NewGuid():N}";

    protected RetryStatusDispatcherIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private void ConfigureDispatcher(WarpServerBuilder<TestContext> config)
    {
        config.UseDispatcher = true;

        // Workers poll ONLY this queue, and the publish below names it explicitly rather than relying on
        // DefaultQueue — the routing this test depends on is then visible at the call site instead of
        // resting on config resolved three layers away.
        config.Queues = [_queue];

        // Two workers is enough to exercise the dispatcher path and keeps this a good neighbour on the
        // shared container (§4.7.1) — this class boots a full server.
        config.WorkerCount = 2;
        config.CompletionBatchSize = 10;
        config.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
    }

    [TimedFact]
    public async Task GivenDispatcherMode_WhenJobRetriesWithDelay_ThenAttemptIsLabelledRetriedNotFailed()
    {
        // Arrange — capture the job-completion meter before anything runs. The queue tag is the isolation
        // key: MeterListener is process-global, so a unique queue keeps concurrent classes out of the counts.
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureDispatcher);

        long retried = 0;
        long failed = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.job.completed", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (!HasTag(tags, "queue", _queue))
            {
                return;
            }

            if (!HasStatus(tags, "retried") && !HasStatus(tags, "failed"))
            {
                return;
            }

            if (HasStatus(tags, "retried"))
            {
                Interlocked.Add(ref retried, value);

                return;
            }

            Interlocked.Add(ref failed, value);
        });
        listener.Start();

        // Act — one throwing job: attempt 1 reschedules (1s delay ⇒ Scheduled), attempt 2 exhausts to Failed.
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters { Queue = _queue });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed);

        // Assert — the rescheduled attempt is a retry, the terminal one is the only failure. Before the fix
        // this read retried=0, failed=2 because the Scheduled reschedule was labelled a failure.
        Interlocked.Read(ref retried).ShouldBe(1);
        Interlocked.Read(ref failed).ShouldBe(1);
    }

    private static bool HasStatus(ReadOnlySpan<KeyValuePair<string, object?>> tags, string value) =>
        HasTag(tags, "status", value);

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, string value)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal)
                && string.Equals(tag.Value?.ToString(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
