using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Warp.Tests.Fixtures;

// Which WarpTestServer hosts are still running against which fixture's database (#364).
//
// xUnit reports a [TimedFact] as failed at its budget and starts the class's next test straight away,
// while the timed-out body is still unwinding: its `await using var server` stops the host AFTER the next
// test's InitializeAsync has already run Respawn on the same database. In that window the old server's
// workers claim the new test's jobs, so the new test sees jobs run by a server it never started (spans
// recorded under another harness, rows finalized by foreign worker ids), and a class-fixture drop can
// deadlock against the old server's open connections. ResetAsync waits here first, so a test never starts
// on a database another test's server is still using.
internal static class RunningTestServers
{
    // Longer than WarpTestServer.StopBudget, so a stop that uses its whole budget still completes first.
    private static readonly TimeSpan WaitBudget = WarpTestServer.StopBudget + TimeSpan.FromSeconds(5);

    private static readonly ConditionalWeakTable<IDatabaseFixture, ConcurrentDictionary<Guid, TaskCompletionSource>> Servers = [];

    public static void Started(IDatabaseFixture fixture, Guid serverId)
        => Servers.GetOrCreateValue(fixture)[serverId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Stopped(IDatabaseFixture fixture, Guid serverId)
    {
        if (Servers.TryGetValue(fixture, out var running) && running.TryRemove(serverId, out var stopped))
        {
            stopped.TrySetResult();
        }
    }

    public static async Task WaitForAllStoppedAsync(IDatabaseFixture fixture)
    {
        if (!Servers.TryGetValue(fixture, out var running) || running.IsEmpty)
        {
            return;
        }

        var stillRunning = running.ToArray();
        TestLifecycleTrace.Record($"Waiting for {stillRunning.Length} server(s) of an earlier test to stop");
        try
        {
            await Task.WhenAll(stillRunning.Select(x => x.Value.Task)).WaitAsync(WaitBudget);
        }
        catch (TimeoutException)
        {
            var ids = string.Join(", ", stillRunning.Where(x => !x.Value.Task.IsCompleted).Select(x => x.Key));

            throw new InvalidOperationException(
                $"WarpTestServer(s) {ids} of an earlier test are still running against this database after {WaitBudget}. A test that does not dispose its server leaves it claiming the next test's jobs.");
        }

        TestLifecycleTrace.Record("Servers of the earlier test stopped");
    }
}
