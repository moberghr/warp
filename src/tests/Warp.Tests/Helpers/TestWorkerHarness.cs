using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Events;
using Warp.Core.Handlers.Generated;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Helpers;

// One WarpWorkerService against the fixture's database, with nothing else running: no host, no server
// tasks, no poll loop. A test inserts a row, calls GetAndProcessJob once and reads the result. This was
// copied, line for line, into every class that tests the worker this way.
internal static class TestWorkerHarness
{
    private static readonly string[] DefaultQueues = ["default"];

    // The Server and Worker rows a claimed job's worker id points at.
    public static async Task SeedServerAndWorkerAsync(IDatabaseFixture fixture, Guid serverId, Guid workerId, CancellationToken ct)
    {
        await using var ctx = fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = workerId,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(ct);
    }

    public static (WarpWorkerService<TestContext> Worker, IServiceScopeFactory ScopeFactory) Create(
        IDatabaseFixture fixture,
        WarpCounterBuffer counterBuffer,
        Guid serverId,
        Guid workerId,
        string[]? queues = null,
        ServerTaskSignals<TestContext>? signals = null)
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddScoped<Warp.Core.Handlers.JobContext>();
        services.AddScoped<Warp.Core.Handlers.IJobContext>(x => x.GetRequiredService<Warp.Core.Handlers.JobContext>());

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = serverId,
            Queues = queues ?? DefaultQueues,
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = queues ?? DefaultQueues,
        };

        var worker = new WarpWorkerService<TestContext>(
            workerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            TestTasks.NullTransport,
            signals ?? TestTasks.NullSignals,
            counterBuffer);

        return (worker, scopeFactory);
    }
}
