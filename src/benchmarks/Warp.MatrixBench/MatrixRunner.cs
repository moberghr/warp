using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.MatrixBench;

/// <summary>
/// Runs one cell of the matrix: a fresh database per round, a warm server, one bulk insert, and everything
/// measured from the moment that insert committed.
/// </summary>
public static class MatrixRunner
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan IdleBeforeInsert = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SettleAfterDrain = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(CellOptions options)
    {
        Console.WriteLine($"cell {options.Name}: jobs={options.Jobs:N0} rounds={options.Rounds} workers={options.Workers} commit={options.Commit}");

        var rounds = new List<RoundResult>();
        string? error = null;
        for (var round = 1; round <= options.Rounds; round++)
        {
            try
            {
                var result = await RunRoundAsync(options, round);
                rounds.Add(result);
                Print(result);
            }
            catch (Exception ex)
            {
                // A harness failure ends the cell but still writes what it has, so the summary can say which
                // cell failed and why instead of showing a hole.
                error = $"round {round}: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine(ex);

                break;
            }
        }

        var cell = new CellResult
        {
            Name = options.Name,
            Ref = options.Ref,
            Commit = options.Commit,
            Provider = options.ProviderName,
            Mode = options.Mode,
            Push = options.Push,
            JobType = options.JobType,
            Jobs = options.Jobs,
            Workers = options.Workers,
            WarmupJobs = options.WarmupJobs,
            StartedAt = DateTime.UtcNow.ToString("O"),
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Error = error,
            Rounds = rounds,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Out));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(options.Out, JsonSerializer.Serialize(cell, ResultJson.Options));
        Console.WriteLine($"wrote {options.Out}");

        // A correctness failure is a result, not a harness failure: the file says so and the job stays green
        // so the summary still runs. Only a round that could not be run at all fails the step.
        return error is null ? 0 : 1;
    }

    private static async Task<RoundResult> RunRoundAsync(CellOptions options, int round)
    {
        var database = $"matrix_r{round}_{Guid.NewGuid():N}";
        var target = await DbStats.CreateDatabaseAsync(options, database);
        var recorder = new ExecutionRecorder();
        var logs = new WarpLogCapture();

        using var host = BuildHost(options, target, recorder, logs);

        // Warp's schema first: EnsureCreated does nothing on a database that already has a table.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatrixContext>().Database.EnsureCreatedAsync();
        }

        await BenchItemStore.CreateAsync(target);
        await host.StartAsync();

        try
        {
            return await MeasureAsync(options, round, target, host, recorder, logs);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<RoundResult> MeasureAsync(
        CellOptions options, int round, BenchTarget target, IHost host, ExecutionRecorder recorder, WarpLogCapture logs)
    {
        // Warm start: the server is up on an empty queue, then a warm-up batch runs and drains, so JIT,
        // pools and plans are paid for before the clock starts.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            for (var i = 0; i < options.WarmupJobs; i++)
            {
                await EnqueuePlainAsync(publisher, options.JobType, i);
            }

            await publisher.SaveChangesAsync();
        }

        var warmup = await WaitForDrainAsync(host, recorder, options.WarmupJobs, Stopwatch.GetTimestamp(), TimeSpan.FromMinutes(5));
        if (warmup.TimedOut)
        {
            throw new TimeoutException($"The {options.WarmupJobs}-job warm-up did not drain within 5 minutes.");
        }

        if (string.Equals(options.JobType, Workloads.Db, StringComparison.Ordinal))
        {
            await BenchItemStore.ResetAsync(target);
        }

        if (string.Equals(options.JobType, Workloads.Mixed, StringComparison.Ordinal))
        {
            await using var scope = host.Services.CreateAsyncScope();

            // Warp's cron has minute resolution (five fields; the scheduler does not read a seconds field),
            // so every minute is the closest it gets to "every few seconds".
            await scope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>()
                .AddOrUpdateRecurringJob(new RecurringBenchJob(), "matrix-recurring", "* * * * *");
        }

        await Task.Delay(IdleBeforeInsert);
        recorder.Reset();
        logs.Reset();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var plan = WorkloadPlan.For(options);
        var insert = Stopwatch.StartNew();
        long marker;
        List<Guid> scheduledIds;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            var batchPublisher = scope.ServiceProvider.GetRequiredService<IBatchPublisher>();
            scheduledIds = await StageAsync(publisher, batchPublisher, options, plan);

            // One SaveChanges on the scope's one DbContext: every row, batches included, in one transaction.
            await publisher.SaveChangesAsync();
            marker = Stopwatch.GetTimestamp();
        }

        insert.Stop();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var dbBeforeTask = DbStats.CaptureAsync(options, target.Database);
        var dbCpuBeforeTask = DbStats.ReadContainerCpuMsAsync(options.DbContainer);
        await Task.WhenAll(dbBeforeTask, dbCpuBeforeTask);

        var harnessStatements = 0L;
        if (scheduledIds.Count > 0)
        {
            // Due five seconds after the marker. Set now rather than at staging, because the insert's own
            // duration is not known until it has committed; staged far in the future, nothing touched them.
            await RescheduleAsync(host, scheduledIds, DateTime.UtcNow.AddSeconds(5));
            harnessStatements++;
        }

        var drain = await WaitForDrainAsync(host, recorder, plan.ExpectedExecutions, marker, options.DrainTimeout);
        harnessStatements += drain.Polls;

        // The cost window runs on past the drain, past the counter-buffer flush (2 s) and the log flush (1 s):
        // work a version defers until after the last job settles is still that version's cost, and cutting
        // the window at the drain would credit it as a saving.
        await Task.Delay(SettleAfterDrain);

        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        var dbAfterTask = DbStats.CaptureAsync(options, target.Database);
        var dbCpuAfterTask = DbStats.ReadContainerCpuMsAsync(options.DbContainer);
        await Task.WhenAll(dbAfterTask, dbCpuAfterTask);

        var checks = await Correctness.CheckAsync(host, target, recorder, plan, drain.TimedOut);

        var finished = recorder.FinishedTimestamps();
        var throughput = Throughput.From(finished, marker);
        var jobs = plan.MeasuredJobs;

        return new RoundResult
        {
            Round = round,
            MeasuredJobs = jobs,
            ExpectedExecutions = plan.ExpectedExecutions,
            InsertSeconds = insert.Elapsed.TotalSeconds,
            TimedOut = drain.TimedOut,
            DrainedSeconds = drain.Seconds,
            FirstToLastSeconds = throughput.FirstToLastSeconds,
            JobsPerSecond = throughput.PerSecond,
            ThroughputP10 = throughput.P10,
            ThroughputP50 = throughput.P50,
            ThroughputP90 = throughput.P90,
            Histogram = throughput.Histogram,
            CpuMsPerJob = cpu / jobs,
            AllocatedBytesPerJob = allocated / (double)jobs,
            Gen0 = gen0,
            Gen1 = gen1,
            Gen2 = gen2,
            Db = DbMetricsFor(await dbBeforeTask, await dbAfterTask, await dbCpuBeforeTask, await dbCpuAfterTask, harnessStatements, jobs),
            Logs = new LogMetrics(logs.Warnings, logs.Errors, logs.Samples),
            Correctness = checks,
        };
    }

    private static DbMetrics? DbMetricsFor(DbSnapshot? before, DbSnapshot? after, double? cpuBefore, double? cpuAfter, long harness, int jobs)
    {
        if (before is null || after is null)
        {
            return null;
        }

        var delta = DbStats.Delta(before, after);
        var statements = delta.Statements - harness;
        var dbCpu = cpuAfter - cpuBefore;

        return new DbMetrics
        {
            Statements = statements,
            HarnessStatements = harness,
            StatementsPerJob = statements / (double)jobs,
            BuffersPerJob = delta.Buffers / (double)jobs,
            WalBytesPerJob = delta.WalBytes / (double?)jobs,
            StatementWalBytesPerJob = before.Plans is null ? delta.StatementWalBytes / (double)jobs : null,
            BatchRequestsPerJob = delta.BatchRequests / (double?)jobs,
            TransactionsPerJob = delta.Transactions / (double?)jobs,
            DbCpuMs = dbCpu,
            DbCpuMsPerJob = dbCpu / jobs,
        };
    }

    private static IHost BuildHost(CellOptions options, BenchTarget target, ExecutionRecorder recorder, WarpLogCapture logs) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
                logging.AddSimpleConsole(x => x.SingleLine = true);
                logging.SetMinimumLevel(LogLevel.Warning);

                // Warp's own warnings and errors are counted and sampled by the capture, and the failing jobs
                // of the mixed workload log a stack trace per attempt; the console keeps everything else.
                logging.AddFilter<ConsoleLoggerProvider>((category, level) => level >= LogLevel.Warning && category?.StartsWith("Warp.", StringComparison.Ordinal) != true);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(recorder);
                services.AddSingleton(target);
                services.AddSingleton<BenchItemStore>();
                services.AddDbContext<MatrixContext>(x =>
                {
                    if (target.Provider == BenchProvider.SqlServer)
                    {
                        x.UseSqlServer(target.ConnectionString);

                        return;
                    }

                    x.UseNpgsql(target.ConnectionString).UseSnakeCaseNamingConvention();
                });

                services.AddWarpServer<MatrixContext>(config =>
                {
                    if (target.Provider == BenchProvider.SqlServer)
                    {
                        config.UseSqlServer();
                    }
                    else
                    {
                        config.UsePostgreSql();
                    }

                    // Only the mixed workload uses the policy addons. Registering them for the plain cells
                    // would put four pipeline behaviours on every empty job and measure those instead.
                    // Retry before Timeout and Concurrency before RateLimit (§2.12).
                    if (string.Equals(options.JobType, Workloads.Mixed, StringComparison.Ordinal))
                    {
                        config.AddRetry();
                        config.AddTimeout();
                        config.AddConcurrency();
                        config.AddRateLimit();
                    }

                    config.WorkerCount = options.Workers;
                    config.UseDispatcher = options.Dispatcher;

                    if (options.Push)
                    {
                        config.UseDatabasePush();
                    }

                    // After UseDatabasePush, which raises a still-default backoff ceiling: one-second polling
                    // with no growth, in every cell, so push on/off differs by the push alone.
                    config.PollingInterval = PollingInterval;
                    config.MaxPollingInterval = PollingInterval;
                    config.PollingIntervalFactor = 1.0;
                });
            })
            .Build();

    private static async Task EnqueuePlainAsync(IPublisher publisher, string jobType, int index)
    {
        if (string.Equals(jobType, Workloads.Db, StringComparison.Ordinal))
        {
            await publisher.Enqueue(new DbJob { Item = index % BenchItemStore.ItemCount });

            return;
        }

        await publisher.Enqueue(new EmptyJob());
    }

    /// <summary>
    /// Stages the measured jobs and returns the scheduled ones' ids. The mixed workload spreads one of each
    /// special kind evenly through the plain jobs, so they are claimed throughout the drain, not in a clump.
    /// </summary>
    private static async Task<List<Guid>> StageAsync(IPublisher publisher, IBatchPublisher batchPublisher, CellOptions options, WorkloadPlan plan)
    {
        var scheduled = new List<Guid>();
        if (!string.Equals(options.JobType, Workloads.Mixed, StringComparison.Ordinal))
        {
            for (var i = 0; i < options.Jobs; i++)
            {
                await EnqueuePlainAsync(publisher, options.JobType, i);
            }

            return scheduled;
        }

        var step = Math.Max(1, plan.PlainJobs / Math.Max(1, plan.Specials));
        var special = 0;
        for (var i = 0; i < plan.PlainJobs; i++)
        {
            await publisher.Enqueue(new EmptyJob());

            if ((i + 1) % step == 0 && special < plan.Specials)
            {
                scheduled.Add(await StageSpecialsAsync(publisher, batchPublisher, special));
                special++;
            }
        }

        while (special < plan.Specials)
        {
            scheduled.Add(await StageSpecialsAsync(publisher, batchPublisher, special));
            special++;
        }

        return scheduled;
    }

    private static async Task<Guid> StageSpecialsAsync(IPublisher publisher, IBatchPublisher batchPublisher, int index)
    {
        await publisher.Publish(new BenchMessage { Index = index });

        var children = Enumerable.Range(0, WorkloadPlan.BatchSize)
            .Select(x => new BatchChildJob { Index = x })
            .ToList();
        await batchPublisher.StartNew(children, $"matrix-batch-{index}");

        var parent = await publisher.Enqueue(new ContinuationParentJob());
        await publisher.Enqueue(new ContinuationChildJob(), parent);

        await publisher.Enqueue(new FailingJob());
        await publisher.Enqueue(new MutexJob());
        await publisher.Enqueue(new SemaphoreJob());
        await publisher.Enqueue(new RateLimitedJob());
        await publisher.Enqueue(new TimeoutJob());

        return await publisher.Schedule(new ScheduledJob(), DateTime.UtcNow.AddHours(1));
    }

    private static async Task RescheduleAsync(IHost host, List<Guid> ids, DateTime due)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MatrixContext>();
        await context.Set<Job>()
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ScheduleTime, due));
    }

    /// <summary>
    /// Waits in process until every expected handler has started, which costs no statement, and only then
    /// polls the database for live rows, which costs one each. The drain time is that poll's.
    /// </summary>
    private static async Task<DrainResult> WaitForDrainAsync(IHost host, ExecutionRecorder recorder, int expectedExecutions, long since, TimeSpan timeout)
    {
        while (recorder.Started < expectedExecutions)
        {
            if (Stopwatch.GetElapsedTime(since) > timeout)
            {
                return new DrainResult(true, null, 0);
            }

            await Task.Delay(20);
        }

        var polls = 0;
        while (true)
        {
            polls++;
            if (!await AnyLiveAsync(host))
            {
                return new DrainResult(false, Stopwatch.GetElapsedTime(since).TotalSeconds, polls);
            }

            if (Stopwatch.GetElapsedTime(since) > timeout)
            {
                return new DrainResult(true, null, polls);
            }

            await Task.Delay(100);
        }
    }

    private static async Task<bool> AnyLiveAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<MatrixContext>()
            .Set<Job>()
            .AnyAsync(x => x.CurrentState == State.Enqueued
                || x.CurrentState == State.Processing
                || x.CurrentState == State.Awaiting
                || x.CurrentState == State.Scheduled);
    }

    private static void Print(RoundResult result)
    {
        Console.WriteLine(
            $"  round {result.Round}: drained {result.DrainedSeconds?.ToString("N2") ?? "TIMEOUT"}s, "
            + $"{result.JobsPerSecond:N0} jobs/s (p10 {result.ThroughputP10:N0} p50 {result.ThroughputP50:N0} p90 {result.ThroughputP90:N0}), "
            + $"cpu {result.CpuMsPerJob:N3} ms/job, alloc {result.AllocatedBytesPerJob:N0} B/job, "
            + $"stmts {result.Db?.StatementsPerJob.ToString("N2") ?? "n/a"}/job, insert {result.InsertSeconds:N1}s, "
            + $"warn {result.Logs.Warnings} err {result.Logs.Errors}, correct={result.Correct}");

        foreach (var check in result.Correctness)
        {
            Console.WriteLine($"    [{(check.Pass ? "pass" : "FAIL")}] {check.Name}: {check.Detail}");
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct DrainResult(bool TimedOut, double? Seconds, int Polls);
}
