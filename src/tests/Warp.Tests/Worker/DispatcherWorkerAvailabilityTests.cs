using Shouldly;
using Warp.Worker;

namespace Warp.Tests.Worker;

/// <summary>
/// Pins the dispatcher's capacity accounting.
/// <para>
/// The property that matters is that a claim is never sized above what the group can hold. The obvious
/// implementation — idle workers plus free channel space — cannot hold that property, because a worker
/// takes its job OUT of the channel before it marks itself busy: for that window the job is counted in
/// neither term and the dispatcher sees capacity that does not exist. These tests describe the invariant
/// in terms of a single reserve/release counter, which is the only shape that survives that interleaving.
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class DispatcherWorkerAvailabilityTests
{
    [TimedFact]
    public void Available_WithNoPrefetch_IsWorkerCount()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 3, prefetchCount: 0);

        availability.Available.ShouldBe(3);
    }

    [TimedFact]
    public void Available_WithPrefetch_AddsPrefetchDepthOnTop()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 3, prefetchCount: 5);

        availability.Available.ShouldBe(8);
    }

    [TimedFact]
    public void Available_AfterReserving_DropsByTheReservedCount()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 3, prefetchCount: 0);

        availability.Reserve(2);

        availability.Available.ShouldBe(1);
    }

    // The interleaving the two-value version gets wrong: a job that has left the channel but whose worker
    // has not yet started it is STILL outstanding. Capacity must stay booked across that whole window, or
    // the dispatcher claims a job with nothing free to run it.
    [TimedFact]
    public void Available_WhileReservedJobIsInFlight_DoesNotReportTheSlotFree()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 1, prefetchCount: 0);

        availability.Reserve(1);

        // Whatever the job is doing — queued, handed over, mid-handler — the slot is spoken for until it
        // is finished with. There is no point at which it reads as free.
        availability.Available.ShouldBe(0);
    }

    [TimedFact]
    public void Available_AfterReleasingEveryReservedJob_IsBackToFullCapacity()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 2, prefetchCount: 2);

        availability.Reserve(4);
        availability.Release();
        availability.Release();
        availability.Release();
        availability.Release();

        availability.Available.ShouldBe(4);
    }

    [TimedFact]
    public void Available_AfterReleasingABatch_ReturnsThatMuchCapacity()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 2, prefetchCount: 2);

        availability.Reserve(4);
        availability.Release(3);

        availability.Available.ShouldBe(3);
    }

    // Concurrency guard: workers release on their own threads while the dispatcher reserves on its own.
    // A non-atomic counter loses updates here and the group either over-claims forever or leaks capacity
    // until it stops claiming at all.
    [TimedFact]
    public async Task Available_UnderConcurrentReserveAndRelease_ReturnsToFullCapacity()
    {
        var availability = new DispatcherWorkerAvailability(workerCount: 8, prefetchCount: 8);
        const int rounds = 2000;

        var reserver = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                availability.Reserve(1);
            }
        });

        var releasers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < rounds / 4; i++)
            {
                availability.Release();
            }
        }));

        await Task.WhenAll(releasers.Prepend(reserver));

        availability.Outstanding.ShouldBe(0);
        availability.Available.ShouldBe(16);
    }
}
