using System.Text;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using EFWorker = Warp.Core.Data.Entities.Worker;
using EFWorkerGroup = Warp.Core.Data.Entities.WorkerGroup;

namespace Warp.Tests.Fixtures;

/// <summary>
/// Builds a multi-line diagnostic dump of test-server state — every Job row with its full JobLog
/// history, every ServerTask row's last run, and every ServerLog entry. Lives on the fixture
/// (not WarpTestServer) because it's a pure DB read; it doesn't matter which server published the
/// rows. Callers pass their own <paramref name="ct"/> so the failure-path call can use a fresh
/// token instead of xunit's already-cancelled one.
/// <para>
/// Runs only on test failure, so size is not the constraint — completeness is. A future-debugger
/// reading a CI flake should not have to wonder whether a missing job was filtered out.
/// </para>
/// </summary>
public static class FixtureDiagnostics
{
    public static Task<string> DumpDiagnosticsAsync(this IDatabaseFixture fixture, string header, CancellationToken ct)
        => DumpAsync(fixture.CreateContext(), header, ct);

    /// <summary>
    /// Failure-dump tail for integration test bases. On any non-passing test result, runs
    /// <paramref name="dumper"/> against the (possibly post-stop) DB and writes the result to
    /// stderr. Useful for tests that don't construct a <see cref="WarpTestServer"/> — DB-only
    /// tests against the fixture, or tests where the failure happened before <c>StartAsync</c>
    /// ran.
    /// <para>
    /// Tests that use <see cref="WarpTestServer"/> get a richer, live pre-stop dump emitted
    /// directly by <see cref="WarpTestServer.DisposeAsync"/> (search stderr for the
    /// <c>[WARP-PRE-STOP-DIAG …]</c> marker). This method's post-shutdown dump is
    /// complementary, not a substitute.
    /// </para>
    /// </summary>
    public static async ValueTask DumpOnFailureAsync(Func<string, CancellationToken, Task<string>> dumper)
    {
        var testState = Xunit.TestContext.Current.TestState;
        if (testState == null || testState.Result == Xunit.TestResult.Passed)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var dump = await dumper(
                $"Test failed ({testState.Result}). Server-state diagnostics (post-shutdown):",
                cts.Token);
            await Console.Error.WriteLineAsync(dump);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Diagnostic dump failed: {ex.Message}");
        }
    }

    private static async Task<string> DumpAsync(TestContext debugCtx, string header, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);

        // Trace events first — purely in-memory, so this section is available even when the
        // fixture's connection pool is exhausted (the very failure mode that drove
        // `PostgreSqlClassFixture.DisposeAsync` to drop databases). When DB queries below
        // fail, the traces alone usually pin down where the test got stuck.
        var testEvents = TestLifecycleTrace.Drain();
        var lifecycleEvents = ServerLifecycleTrace.Drain();

        sb.AppendLine();
        sb.AppendLine($"Test lifecycle ({testEvents.Count} events):");
        foreach (var grouped in testEvents.GroupBy(e => e.TestName))
        {
            sb.AppendLine($"  Test {grouped.Key}:");
            foreach (var e in grouped.OrderBy(x => x.Timestamp))
            {
                sb.AppendLine($"    [{e.Timestamp:HH:mm:ss.fff}] {e.Event}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Server lifecycle ({lifecycleEvents.Count} events):");
        foreach (var grouped in lifecycleEvents.GroupBy(e => e.ServerId))
        {
            sb.AppendLine($"  Server {grouped.Key}:");
            foreach (var e in grouped.OrderBy(x => x.Timestamp))
            {
                sb.AppendLine($"    [{e.Timestamp:HH:mm:ss.fff}] {e.Event}");
            }
        }

        // DB-backed section — best effort. If the fixture's pool is exhausted (or any other
        // connection-level failure), surface the error inline rather than throwing out of
        // the dump entirely. The trace section above is still authoritative.
        try
        {
            await AppendDbStateAsync(sb, debugCtx, ct);
        }
#pragma warning disable CA1031 // diagnostic dump must never throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            sb.AppendLine();
            sb.AppendLine($"⚠ DB-backed diagnostic queries failed: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine("Trace events above remain authoritative.");
        }

        return sb.ToString();
    }

    private static async Task AppendDbStateAsync(StringBuilder sb, TestContext debugCtx, CancellationToken ct)
    {
        var stateHistogram = await debugCtx.Set<Job>()
            .AsNoTracking()
            .GroupBy(j => new { j.Type, j.CurrentState })
            .Select(g => new { g.Key.Type, g.Key.CurrentState, Count = g.Count() })
            .ToListAsync(ct);

        var allJobs = await debugCtx.Set<Job>()
            .AsNoTracking()
            .OrderBy(j => j.CreateTime)
            .Select(j => new
            {
                j.Id,
                j.Kind,
                j.Type,
                j.CurrentState,
                j.ParentJobId,
                j.Queue,
                j.CreateTime,
                j.ScheduleTime,
                j.CurrentWorkerId,
                j.LastKeepAlive,
                j.ExpireAt,
                j.CancellationMode,
                j.Metadata,
            })
            .ToListAsync(ct);

        var allLogs = await debugCtx.Set<JobLog>()
            .AsNoTracking()
            .OrderBy(l => l.Timestamp)
            .Select(l => new { l.JobId, l.Timestamp, l.EventType, l.Level, l.Message, l.Exception, l.WorkerId, l.DurationMs })
            .ToListAsync(ct);

        var servers = await debugCtx.Set<Server>()
            .AsNoTracking()
            .OrderBy(s => s.StartedTime)
            .ToListAsync(ct);

        var workers = await debugCtx.Set<EFWorker>()
            .AsNoTracking()
            .OrderBy(w => w.StartedTime)
            .ToListAsync(ct);

        var workerGroups = await debugCtx.Set<EFWorkerGroup>()
            .AsNoTracking()
            .OrderBy(g => g.ServerId)
            .ToListAsync(ct);

        var recurringJobs = await debugCtx.Set<RecurringJob>()
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var recurringJobLogs = await debugCtx.Set<RecurringJobLog>()
            .AsNoTracking()
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);

        var counters = await debugCtx.Set<Counter>()
            .AsNoTracking()
            .OrderBy(c => c.Key)
            .ToListAsync(ct);

        var statistics = await debugCtx.Set<Statistic>()
            .AsNoTracking()
            .OrderBy(s => s.Key)
            .ToListAsync(ct);

        var circuitStates = await debugCtx.Set<CircuitBreakerState>()
            .AsNoTracking()
            .OrderBy(s => s.GroupKey)
            .ToListAsync(ct);

        var serverTasks = await debugCtx.Set<ServerTask>()
            .AsNoTracking()
            .OrderBy(t => t.TaskName)
            .Select(t =>
                new { t.TaskName, t.IntervalSeconds, t.LastRun, t.LastStatus, t.LastMessage, t.LastDurationMs })
            .ToListAsync(ct);

        var serverLogs = await debugCtx.Set<ServerLog>()
            .AsNoTracking()
            .OrderBy(l => l.Timestamp)
            .Select(l =>
                new { l.Timestamp, l.Status, l.Message, l.DurationMs, TaskName = l.ServerTask != null ? l.ServerTask.TaskName : null })
            .ToListAsync(ct);

        var logsByJob = allLogs.GroupBy(l => l.JobId).ToDictionary(g => g.Key, g => g.ToList());

        sb.AppendLine();
        sb.AppendLine($"Job state histogram ({stateHistogram.Count} groups):");
        foreach (var g in stateHistogram.OrderBy(x => x.Type).ThenBy(x => x.CurrentState))
        {
            sb.AppendLine($"  {ShortTypeName(g.Type)} {g.CurrentState} = {g.Count}");
        }

        sb.AppendLine();
        sb.AppendLine($"All jobs ({allJobs.Count}):");
        foreach (var j in allJobs)
        {
            sb.AppendLine(
                $"  {j.Id} kind={j.Kind} type={ShortTypeName(j.Type)} state={j.CurrentState} queue={j.Queue} " +
                $"parent={j.ParentJobId} createTime={j.CreateTime:HH:mm:ss.fff} scheduleTime={j.ScheduleTime:HH:mm:ss.fff} " +
                $"worker={j.CurrentWorkerId} keepAlive={j.LastKeepAlive:HH:mm:ss.fff} expireAt={j.ExpireAt:HH:mm:ss.fff} " +
                $"cancel={j.CancellationMode} metadata={j.Metadata}");
            if (logsByJob.TryGetValue(j.Id, out var logs))
            {
                foreach (var l in logs)
                {
                    sb.AppendLine($"    [{l.Timestamp:HH:mm:ss.fff}] {l.Level} {l.EventType} worker={l.WorkerId} duration={l.DurationMs:0.#}ms — {l.Message}");
                    if (!string.IsNullOrEmpty(l.Exception))
                    {
                        sb.AppendLine($"      {IndentLines(l.Exception)}");
                    }
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Servers ({servers.Count}):");
        foreach (var s in servers)
        {
            sb.AppendLine($"  {s.Id} name={s.ServerName} started={s.StartedTime:HH:mm:ss.fff} lastHeartbeat={s.LastHeartbeatTime:HH:mm:ss.fff} services={s.ServiceCount} pausedAt={s.PausedAt:HH:mm:ss.fff}");
        }

        sb.AppendLine();
        sb.AppendLine($"WorkerGroups ({workerGroups.Count}):");
        foreach (var g in workerGroups)
        {
            sb.AppendLine($"  {g.Id} server={g.ServerId} workers={g.WorkerCount} queues={g.Queues} pollMs={g.PollingIntervalMs} pausedAt={g.PausedAt:HH:mm:ss.fff}");
        }

        sb.AppendLine();
        sb.AppendLine($"Workers ({workers.Count}):");
        foreach (var w in workers)
        {
            sb.AppendLine($"  {w.Id} server={w.ServerId} group={w.WorkerGroupId} started={w.StartedTime:HH:mm:ss.fff} lastHeartbeat={w.LastHeartbeatTime:HH:mm:ss.fff}");
        }

        sb.AppendLine();
        sb.AppendLine($"RecurringJobs ({recurringJobs.Count}):");
        foreach (var r in recurringJobs)
        {
            sb.AppendLine($"  {r.Id} name={r.Name} type={ShortTypeName(r.Type)} cron={r.Cron} queue={r.Queue} next={r.NextExecution:HH:mm:ss.fff} last={r.LastExecution:HH:mm:ss.fff} disabled={r.DisabledAt:HH:mm:ss.fff}");
        }

        sb.AppendLine();
        sb.AppendLine($"RecurringJobLog ({recurringJobLogs.Count}):");
        foreach (var l in recurringJobLogs)
        {
            sb.AppendLine($"  [{l.CreatedAt:HH:mm:ss.fff}] recurring={l.RecurringJobId} job={l.JobId} skipped={l.Skipped}");
        }

        sb.AppendLine();
        sb.AppendLine($"Counters ({counters.Count}):");
        foreach (var c in counters)
        {
            sb.AppendLine($"  {c.Key} = {c.Value}");
        }

        sb.AppendLine();
        sb.AppendLine($"Statistics ({statistics.Count}):");
        foreach (var s in statistics)
        {
            sb.AppendLine($"  {s.Key} = {s.Value}");
        }

        sb.AppendLine();
        sb.AppendLine($"CircuitBreakerState ({circuitStates.Count}):");
        foreach (var s in circuitStates)
        {
            sb.AppendLine($"  {s.GroupKey} state={s.State} failures={s.FailureCount} openUntil={s.OpenUntil:HH:mm:ss.fff} lastFailure={s.LastFailureAt:HH:mm:ss.fff}");
        }

        sb.AppendLine();
        sb.AppendLine($"ServerTask rows ({serverTasks.Count}):");
        foreach (var t in serverTasks)
        {
            sb.AppendLine($"  {t.TaskName} interval={t.IntervalSeconds}s lastRun={t.LastRun:HH:mm:ss.fff} status={t.LastStatus} duration={t.LastDurationMs:0.#}ms message={t.LastMessage}");
        }

        sb.AppendLine();
        sb.AppendLine($"ServerLog entries ({serverLogs.Count}, oldest first):");
        foreach (var l in serverLogs)
        {
            sb.AppendLine($"  [{l.Timestamp:HH:mm:ss.fff}] {l.TaskName ?? "<no-task>"} {l.Status} {l.DurationMs:0.#}ms — {l.Message}");
        }
    }

    private static string ShortTypeName(string? assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return "<null>";
        }

        var commaIdx = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);
        var fullName = commaIdx > 0 ? assemblyQualifiedName[..commaIdx] : assemblyQualifiedName;
        var dotIdx = fullName.LastIndexOf('.');

        return dotIdx > 0 ? fullName[(dotIdx + 1)..] : fullName;
    }

    private static string IndentLines(string text)
        => text.Replace("\n", "\n      ", StringComparison.Ordinal);
}
