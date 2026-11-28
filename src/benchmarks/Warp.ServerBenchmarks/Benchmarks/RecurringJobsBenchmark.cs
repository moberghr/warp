using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Many recurring jobs falling due on the same tick.
/// <para>
/// <c>RecurringJobScheduler</c> visits every due definition in one pass of one server task, inside the task
/// host's lock transaction (rule 8.9): it creates each firing's job, its log row and its recurring-log row and
/// moves the definition on. Its cost grows with the number of definitions due at once, which is what a
/// deployment with hundreds of definitions on the same schedule (every hour on the hour) pays at each
/// occurrence, and no other scenario reaches the scheduler at all.
/// </para>
/// <para>
/// 500 definitions are seeded once, on a schedule that does not come round during the run. Each operation
/// makes all of them due at once, then waits until every firing's job has run; the scheduler's interval is
/// 1 s, so the wait for its next tick is at most a second. Statements per job are per firing, the job's own
/// execution included.
/// </para>
/// </summary>
[CiScenario(
    "Recurring jobs at scale",
    "500 recurring definitions fall due on the same tick; the scheduler (every 1 s) fires them all and 10 workers run the 500 jobs.",
    "The recurring scheduler visits every due definition in one server-task pass, so its cost grows with the number due at once. No other scenario reaches it.",
    Order = 82,
    ClockBound = true)]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class RecurringJobsBenchmark
{
    // Midnight on 1 January: never comes round while the benchmark runs, so a definition fires only when the
    // operation makes it due.
    private const string NeverDuringTheRun = "0 0 1 1 *";

    private PostgresServerFixture _fixture = null!;

    [Params(500)]
    public int JobCount { get; set; }

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
            useDispatcher: false,
            provider: Provider,
            configure: config => config.RecurringJobSchedulerInterval = TimeSpan.FromSeconds(1));

        await SeedDefinitionsAsync();

        await MakeAllDueAsync();
        await WaitForFiringsAsync();
        await CleanAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        CleanAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task FireRecurringJobs()
    {
        await MakeAllDueAsync();
        await WaitForFiringsAsync();
    }

    private async Task SeedDefinitionsAsync()
    {
        await using var scope = _fixture.Host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();
        var now = DateTime.UtcNow;
        var message = JsonSerializer.Serialize(new EmptyRequest());
        var type = typeof(EmptyRequest).AssemblyQualifiedName;

        // Inserted directly rather than through AddOrUpdateRecurringJob, which takes a session lock and saves
        // per definition: registration is not what is measured.
        context.Set<RecurringJob>().AddRange(Enumerable.Range(0, JobCount).Select(x => new RecurringJob
        {
            Name = $"bench-recurring-{x}",
            Message = message,
            Type = type,
            Cron = NeverDuringTheRun,
            CreatedAt = now,
            NextExecution = now.AddYears(1),
        }));
        await context.SaveChangesAsync();
    }

    // Harness work (it stands in for the clock reaching the definitions' due time), not counted as Warp's.
    private Task MakeAllDueAsync()
    {
        var due = DateTime.UtcNow.AddMinutes(-1);

        return _fixture.HarnessAsync(x => x.Set<RecurringJob>()
            .TagWith(Harness.Tag)
            .ExecuteUpdateAsync(y => y.SetProperty(z => z.NextExecution, due)));
    }

    // Every definition fired (moved off the past due time) and every job it created finished.
    private async Task WaitForFiringsAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var now = DateTime.UtcNow;
            var pending = await _fixture.HarnessAsync(x => x.Set<RecurringJob>()
                .TagWith(Harness.Tag)
                .Where(y => y.NextExecution < now)
                .CountAsync());

            if (pending == 0)
            {
                await _fixture.WaitForCompletion();

                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The recurring scheduler did not fire every definition within two minutes.");
    }

    // The recurring log references the jobs, so it goes first; the definitions stay for the next iteration.
    private async Task CleanAsync()
    {
        await _fixture.HarnessAsync(x => x.Set<RecurringJobLog>().TagWith(Harness.Tag).ExecuteDeleteAsync());
        await _fixture.CleanJobTables();
    }
}
