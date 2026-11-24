using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class ServerTaskRegistrationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerTaskRegistrationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task StartAsync_SeveralTasks_RegistersThemAllWithOneInsert()
    {
        // A server registers every task's ServerTask row in one batched save before the task loops
        // start, instead of each loop sending its own SELECT + INSERT on its first tick: N servers x
        // 14 tasks starting together were the slowest statements in the database during startup.
        var serverId = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Server>().Add(new Server { Id = serverId, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
            await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        string table;
        await using (var model = _fixture.CreateContext())
        {
            table = model.Model.FindEntityType(typeof(ServerTask))!.GetTableName()!;
        }

        // Only this test's contexts are counted: other test classes run in parallel.
        var ours = new ConcurrentDictionary<DbContext, bool>();
        var inserts = 0;
        using var listener = new InsertCounter(ours, table, () => Interlocked.Increment(ref inserts));
        using var subscription = DiagnosticListener.AllListeners.Subscribe(listener);

        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var context = _fixture.CreateContext();
            ours[context] = true;

            return context;
        });
        services.AddTestServerContext<TestContext>();
        services.AddScoped<IServerTask, FirstTask>();
        services.AddScoped<IServerTask, SecondTask>();
        await using var provider = services.BuildServiceProvider();

        using var host = new ServerTaskHost<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NoopLockProvider(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            Options.Create(new WarpServerConfiguration { ServerId = serverId }),
            new ServerTaskSignals<TestContext>());

        // Act
        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        try
        {
            await WarpTestServer.WaitUntil(
                async () =>
                {
                    await using var ctx = _fixture.CreateContext();

                    return await ctx.Set<ServerTask>().CountAsync(x => x.ServerId == serverId) == 2;
                },
                TimeSpan.FromSeconds(5),
                Xunit.TestContext.Current.CancellationToken);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        await using var read = _fixture.CreateContext();
        var rows = await read.Set<ServerTask>()
            .AsNoTracking()
            .Where(x => x.ServerId == serverId)
            .OrderBy(x => x.TaskName)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        rows.Select(x => x.TaskName).ShouldBe(["first", "second"]);
        rows.Select(x => x.IntervalSeconds).ShouldBe([60.0, 120.0]);
        inserts.ShouldBe(1);
    }

    private sealed class FirstTask : IServerTask
    {
        public string Name => "first";

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => TimeSpan.FromMinutes(1);

        public Task<string?> ExecuteAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class SecondTask : IServerTask
    {
        public string Name => "second";

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => TimeSpan.FromMinutes(2);

        public Task<string?> ExecuteAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class NoopLockProvider : IWarpLockProvider
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new NoopHandle());

        private sealed class NoopHandle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Counts executed INSERT commands into the ServerTask table that came from this test's contexts.
    private sealed class InsertCounter(ConcurrentDictionary<DbContext, bool> ours, string table, Action onInsert)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, DbLoggerCategory.Name, StringComparison.Ordinal))
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (string.Equals(value.Key, RelationalEventId.CommandExecuted.Name, StringComparison.Ordinal)
                && value.Value is CommandExecutedEventData data
                && data.Context is { } context
                && ours.ContainsKey(context)
                && data.Command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && data.Command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                onInsert();
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
            }
        }
    }
}
