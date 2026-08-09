using System.Globalization;
using Warp.PerfTest;

// Defaults — bump via CLI args:
//   --jobs 10000 --scenario dispatcher-push       (workload mode)
//   --mode idle --idle-seconds 30                 (idle-chatter mode, 4-scenario matrix)
//   --mode idle --scenario dispatcher-push --disable backlog-sampler,slo-evaluator --repeat 3
//                                                 (per-loop attribution; --disable post-may
//                                                  expands to every loop added after the
//                                                  2026-05-14 baseline. Default: nothing
//                                                  disabled — the matrix measures defaults.)
var jobCount = 1000;
var idleSeconds = 30;
var filter = (string?)null;
var mode = "workload";
var verbose = false;
var repeat = 1;
var completionFlushMs = (int?)null;
var sqlServer = false;
var singleBurst = false;
var prefetchCount = (int?)null;
var disabled = new List<string>();

var queue = new Queue<string>(args);
while (queue.Count > 0)
{
    var flag = queue.Dequeue();
    switch (flag)
    {
        case "--jobs" when queue.Count > 0:
            jobCount = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
            break;
        case "--idle-seconds" when queue.Count > 0:
            idleSeconds = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
            break;
        case "--scenario" when queue.Count > 0:
            filter = queue.Dequeue();
            break;
        case "--mode" when queue.Count > 0:
            mode = queue.Dequeue();
            break;
        case "--repeat" when queue.Count > 0:
            repeat = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
            break;
        case "--disable" when queue.Count > 0:
            disabled.AddRange(queue.Dequeue().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            break;
        case "--flush-ms" when queue.Count > 0:
            completionFlushMs = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
            break;
        case "--prefetch" when queue.Count > 0:
            prefetchCount = int.Parse(queue.Dequeue(), CultureInfo.InvariantCulture);
            break;
        case "--single-burst":
            singleBurst = true;
            break;
        case "--sqlserver":
            sqlServer = true;
            break;
        case "--verbose":
            verbose = true;
            break;
        default:
            break;
    }
}

if (string.Equals(mode, "idle", StringComparison.OrdinalIgnoreCase))
{
    await RunIdleAsync(idleSeconds, filter, verbose, repeat, IdleLoopSwitches.Expand(disabled));
    return;
}

await RunWorkloadAsync(jobCount, filter, completionFlushMs, verbose, sqlServer, singleBurst, prefetchCount);

static async Task RunWorkloadAsync(int jobCount, string? filter, int? completionFlushMs, bool verbose, bool sqlServer, bool singleBurst, int? prefetchCount)
{
    var scenarios = new (string Name, bool UseDispatcher, bool EnableDatabasePush)[]
    {
        ("workers-poll", false, false),
        ("dispatcher-poll", true, false),
        ("dispatcher-push", true, true),
    };

    var results = new List<PerfResult>();
    foreach (var (name, useDispatcher, push) in scenarios)
    {
        if (filter != null && !string.Equals(filter, name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        await Console.Error.WriteLineAsync($"[{DateTime.UtcNow:HH:mm:ss}] Running workload scenario '{name}' with {jobCount} jobs...");
        var result = await PerfScenario.RunAsync(name, jobCount, useDispatcher, push, completionFlushMs, verbose, sqlServer, singleBurst, prefetchCount);
        await Console.Error.WriteLineAsync($"[{DateTime.UtcNow:HH:mm:ss}]   duration={result.Duration}, total commands={result.Total}");
        results.Add(result);
    }

    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync("| Scenario          | Jobs  | Duration | Enqueue |  Drain | jobs/s | SELECT | UPDATE | INSERT | DELETE | Other | Total | Batches |");
    await Console.Out.WriteLineAsync("|-------------------|-------|----------|--------:|-------:|-------:|-------:|-------:|-------:|-------:|------:|------:|--------:|");
    foreach (var r in results)
    {
        var duration = r.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var burst1 = r.Enqueue.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var burst2 = r.Drain.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var burstSeconds = r.Drain.TotalSeconds;
        var rate = (burstSeconds > 0 ? r.Jobs / burstSeconds : 0).ToString("0", CultureInfo.InvariantCulture);
        await Console.Out.WriteLineAsync(
            $"| {r.Name,-17} | {r.Jobs,5} | {duration,8} | {burst1,6} | {burst2,6} | {rate,6} | {r.Select,6} | {r.Update,6} | {r.Insert,6} | {r.Delete,6} | {r.Other,5} | {r.Total,5} | {r.ServerBatchRequests,7} |");
    }

    if (!verbose)
    {
        return;
    }

    foreach (var r in results)
    {
        if (r.CapturedByText is null || r.CapturedByText.Count == 0)
        {
            continue;
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"### Scenario '{r.Name}' — distinct queries (count desc, top 25)");
        await Console.Out.WriteLineAsync();
        foreach (var (text, count) in r.CapturedByText.OrderByDescending(kvp => kvp.Value).Take(25))
        {
            var trimmed = text.Length > 220 ? text[..220] + " …" : text;
            await Console.Out.WriteLineAsync($"- **{count}x** `{trimmed}`");
        }
    }
}

static async Task RunIdleAsync(int idleSeconds, string? filter, bool verbose, int repeat, IReadOnlyList<string> disabled)
{
    var scenarios = new (string Name, bool UseDispatcher, bool EnableDatabasePush)[]
    {
        ("workers-poll", false, false),
        ("workers-push", false, true),
        ("dispatcher-poll", true, false),
        ("dispatcher-push", true, true),
    };

    var results = new List<IdleResult>();
    foreach (var (name, useDispatcher, push) in scenarios)
    {
        if (filter != null && !string.Equals(filter, name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var suffix = disabled.Count == 0 ? string.Empty : $" (disabled: {string.Join(",", disabled)})";
        for (var run = 1; run <= repeat; run++)
        {
            await Console.Error.WriteLineAsync($"[{DateTime.UtcNow:HH:mm:ss}] Running idle scenario '{name}'{suffix} run {run}/{repeat} for {idleSeconds}s...");
            var result = await IdleScenario.RunAsync(name, idleSeconds, useDispatcher, push, verbose, disabled);
            await Console.Error.WriteLineAsync($"[{DateTime.UtcNow:HH:mm:ss}]   total={result.Total}, q/s={result.QueriesPerSecond:0.0}");
            results.Add(result);
        }
    }

    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync($"Idle measurement window: {idleSeconds}s per scenario (post 3s warm-up).");
    await Console.Out.WriteLineAsync();
    if (disabled.Count > 0)
    {
        await Console.Out.WriteLineAsync($"Disabled loops: {string.Join(", ", disabled)}");
        await Console.Out.WriteLineAsync();
    }

    await Console.Out.WriteLineAsync("| Scenario          | Dispatcher | Push  | SELECT | UPDATE | INSERT | DELETE | Other | Total | Queries/sec |");
    await Console.Out.WriteLineAsync("|-------------------|------------|-------|-------:|-------:|-------:|-------:|------:|------:|------------:|");
    foreach (var r in results)
    {
        var dispatcher = r.UseDispatcher ? "on" : "off";
        var push = r.EnableDatabasePush ? "on" : "off";
        var qps = r.QueriesPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
        await Console.Out.WriteLineAsync(
            $"| {r.Name,-17} | {dispatcher,-10} | {push,-5} | {r.Select,6} | {r.Update,6} | {r.Insert,6} | {r.Delete,6} | {r.Other,5} | {r.Total,5} | {qps,11} |");
    }

    if (!verbose)
    {
        return;
    }

    foreach (var r in results)
    {
        if (r.ServerLogByTask is { Count: > 0 })
        {
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"### Scenario '{r.Label}' — ServerLog rows written in-window, by task");
            await Console.Out.WriteLineAsync();
            foreach (var (task, count) in r.ServerLogByTask.OrderByDescending(kvp => kvp.Value))
            {
                await Console.Out.WriteLineAsync($"- **{count}×** {task}");
            }
        }

        if (r.CapturedByText is null || r.CapturedByText.Count == 0)
        {
            continue;
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"### Scenario '{r.Name}' — distinct queries (count desc)");
        await Console.Out.WriteLineAsync();
        foreach (var (text, count) in r.CapturedByText.OrderByDescending(kvp => kvp.Value))
        {
            await Console.Out.WriteLineAsync($"- **{count}×** `{text}`");
        }
    }
}
