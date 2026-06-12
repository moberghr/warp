using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Notifications;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Measures per-job memory allocation of the worker execution path.
/// Calls GetAndProcessJob() directly on the benchmark thread so [MemoryDiagnoser]
/// captures all allocations precisely.
///
/// No hosted services running — isolates the worker path from background task noise.
/// </summary>
[Config(typeof(ComponentBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class WorkerMemoryBenchmark
{
    private PostgresServerFixture _fixture = null!;
    private WarpWorkerService<TestContext> _workerService = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeWithoutHostedServicesAsync();

        // Construct a WarpWorkerService manually (it's not registered in DI)
        var services = _fixture.Host.Services;
        _workerService = new WarpWorkerService<TestContext>(
            Guid.NewGuid(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILogger<WarpWorkerService<TestContext>>>(),
            services.GetRequiredService<IOptions<WarpServerConfiguration>>(),
            new WorkerGroupConfiguration { Queues = ["default"], WorkerCount = 1 },
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IWarpSqlQueries<TestContext>>(),
            services.GetRequiredService<IWarpNotificationTransport>(),
            services.GetRequiredService<ServerTaskSignals<TestContext>>());

        // Register a server + worker in the DB (required for job processing)
        await using var scope = services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var now = DateTime.UtcNow;
        var config = services.GetRequiredService<IOptions<WarpServerConfiguration>>().Value;
        ctx.Set<Server>().Add(new Server
        {
            Id = config.ServerId,
            ServerName = "benchmark-server",
            StartedTime = now,
            LastHeartbeatTime = now,
            ServiceCount = 1,
        });
        await ctx.SaveChangesAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationSetup]
    public void InsertJob()
    {
        using var scope = _fixture.Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            Type = typeof(EmptyRequest).AssemblyQualifiedName!,
            Message = JsonSerializer.Serialize(new EmptyRequest()),
            CurrentState = State.Enqueued,
            CreateTime = now,
            ScheduleTime = now.AddMinutes(-1),
            Queue = "default",
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// Full worker cycle: fetch job (with row lock + transaction), deserialize,
    /// execute handler, finalize state, write logs + counters. All on the benchmark thread.
    /// </summary>
    [Benchmark]
    public async Task GetAndProcessJob()
    {
        await _workerService.GetAndProcessJob(CancellationToken.None);
    }
}
