using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a backlog of future-dated jobs costs when it all comes due at once.
/// <para>
/// Scheduled jobs land in <c>State.Scheduled</c> and are flipped to <c>Enqueued</c> by
/// <c>ScheduledJobActivation</c> (rule 2.8). The audit in <c>docs/perf-results.md</c> lists that task
/// among the sweeps that are "unbounded but bounded in practice" — it has no <c>LIMIT</c>, and what
/// keeps it small is the assumption that only so many rows come due at the same instant. This arm
/// stops assuming: every job is scheduled for the same moment, so the activation has the whole
/// backlog to flip in one statement.
/// </para>
/// <para>
/// The jobs are published a second in the past, not the future. Waiting out a real delay would measure
/// the clock rather than the code, and a row whose <c>ScheduleTime</c> has already passed is exactly
/// what the activation sweep exists to pick up.
/// </para>
/// </summary>
[CiScenario(
    "Scheduled backlog",
    "1,000 jobs are scheduled for the same moment, already past, so the activation task moves the whole backlog to Enqueued at once; 10 workers then drain it.",
    "The activation sweep has no row limit. This shows what a large backlog coming due at once costs.",
    Order = 80)]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class ScheduledActivationBenchmark
{
    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        // The sweep runs on its interval, not on a signal (rule 2.8), so at the 10 s default most of each
        // iteration would be spent waiting for its next tick. 1 s keeps the scenario about the sweep.
        await _fixture.InitializeAsync(
            workerCount: 10,
            useDispatcher: false,
            configure: x => x.ScheduledActivationInterval = TimeSpan.FromSeconds(1));

        await PublishAsync(100);
        await _fixture.WaitForCompletion();
        await _fixture.CleanJobTables();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task ActivateAndProcess()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    /// <summary>
    /// Inserts the backlog directly as <see cref="State.Scheduled"/>, already due.
    /// <para>
    /// Not through the publisher. A job published with a past <c>ScheduleTime</c> is created straight
    /// into <c>Enqueued</c> — <c>JobHelper</c> schedules only a FUTURE time — so this scenario used to be
    /// the plain drain again and never reached the activation sweep at all. Rows in Scheduled with a time
    /// that has passed are exactly what the sweep exists to pick up.
    /// </para>
    /// </summary>
    private async Task PublishAsync(int count)
    {
        var due = DateTime.UtcNow.AddSeconds(-1);
        var type = typeof(EmptyRequest).AssemblyQualifiedName;

        await using var scope = _fixture.Host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();
        context.Set<Job>().AddRange(Enumerable.Range(0, count).Select(_ => new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Type = type,
            Message = "{}",
            CreateTime = due,
            ScheduleTime = due,
            Queue = "default",
        }));
        await context.SaveChangesAsync();
    }
}
