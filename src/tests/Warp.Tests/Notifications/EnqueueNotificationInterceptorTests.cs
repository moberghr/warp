using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Issue #322: jobs committed by a plain DbContext.SaveChangesAsync (not IPublisher.SaveChangesAsync)
// never woke a worker — neither the cross-process NOTIFY nor the in-process signal. The interceptor
// AddWarp puts on TContext now signals this process and announces them for the relay, and holds both
// until an explicit transaction commits. Resolved through the real AddWarp registration so the options
// wiring is what ships. The relay's own publishing is covered by EnqueueNotificationRelayTests.
[GenerateDatabaseTests]
public abstract class EnqueueNotificationInterceptorTestsBase : IAsyncLifetime
{
    private const string Queue = "interceptor-q";

    private readonly IDatabaseFixture _fixture;

    protected EnqueueNotificationInterceptorTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task SaveChangesAsync_AfterEnqueue_SendsOneWakeUpAndSignalsLocally()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        Announced(provider, Queue).ShouldBe(1);
        local.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChanges_Sync_AfterEnqueue_SendsWakeUpAndSignalsLocally()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);

        // The synchronous path is what this test covers.
#pragma warning disable MA0042, S6966
        db.SaveChanges();
#pragma warning restore MA0042, S6966

        local.Count.ShouldBe(1);
        Announced(provider, Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task PublisherSaveChangesAsync_SendsExactlyOneWakeUp()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var local = CountLocalSignals(provider);

        await publisher.Enqueue(new UnitRequest(), Queue);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        Announced(provider, Queue).ShouldBe(1);
        local.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_InsideExplicitTransaction_WaitsForCommit()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(ct);

        Announced(provider, Queue).ShouldBe(0);

        await tx.CommitAsync(ct);

        Announced(provider, Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_TwoSavesInOneTransaction_SendOneWakeUpOnCommit()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await publisher.Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(ct);
        await publisher.Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        Announced(provider, Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_InsideRolledBackTransaction_SendsNothing()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
            await db.SaveChangesAsync(ct);
            await tx.RollbackAsync(ct);
        }

        Announced(provider, Queue).ShouldBe(0);
        local.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task SaveChangesAsync_TransactionDisposedWithoutCommit_DoesNotLeakIntoTheNextOne()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using (await db.Database.BeginTransactionAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
            await db.SaveChangesAsync(ct);
        }

        db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        Announced(provider, Queue).ShouldBe(0);
    }

    [TimedFact]
    public async Task SaveChangesAsync_WithoutStagedJobs_SendsNothing()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        Announced(provider).ShouldBe(0);
        local.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task AddWarp_CalledTwice_RegistersTheInterceptorOnce()
    {
        await using var provider = BuildProvider(services => services.AddWarp<TestContext>());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        Announced(provider, Queue).ShouldBe(1);
    }

    // The push transport's factory used to build DbContextOptions<TContext>, whose factory adds the
    // interceptor; an interceptor that took the transport in its constructor closed that loop and the
    // container deadlocked building the transport (every push test hung). Both halves are gone now —
    // the factory reads WarpDatabaseConnection, the interceptor holds no transport — and this pins it.
    // Bounded so a regression fails here instead of hanging the run.
    [TimedFact]
    public async Task UseDatabasePush_ResolvingTransportAndContext_DoesNotDeadlock()
    {
        await using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options =>
        {
            if (isPostgres)
            {
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });
        services.AddWarp<TestContext>(opt =>
        {
            if (isPostgres)
            {
                opt.UsePostgreSql();
            }
            else
            {
                opt.UseSqlServer();
            }

            opt.UseDatabasePush();
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var resolve = Task.Run(
            () =>
            {
                using var scope = provider.CreateScope();
                var transport = scope.ServiceProvider.GetRequiredService<IWarpNotificationTransport>();
                var db = scope.ServiceProvider.GetRequiredService<TestContext>();

                return (transport, db);
            },
            Xunit.TestContext.Current.CancellationToken);

        var (resolvedTransport, resolvedContext) = await resolve.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        resolvedTransport.ShouldNotBeOfType<NullNotificationTransport>();
        resolvedContext.ShouldNotBeNull();
    }

    private static LocalSignalCounter CountLocalSignals(ServiceProvider provider)
    {
        var counter = new LocalSignalCounter();
        provider.GetRequiredService<ServerTaskSignals<TestContext>>().Subscribe(ServerTaskSignal.JobEnqueued, counter.Increment);

        return counter;
    }

    // Announcements are switched on as UseDatabasePush() does, but no host runs the relay, so what the
    // interceptor announced stays in the channel for the test to read.
    private ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options =>
        {
            if (isPostgres)
            {
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });
        services.AddWarp<TestContext>();
        EnqueueNotificationRelay.Register(services);
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class LocalSignalCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    // Drains what the interceptor announced so far; counts JobEnqueued for one queue, or everything.
    private static int Announced(ServiceProvider provider, string? queue = null)
    {
        var reader = provider.GetRequiredService<EnqueueAnnouncements>().Reader;
        var count = 0;
        while (reader.TryRead(out var notification))
        {
            if (queue == null || (notification.Kind == NotificationKind.JobEnqueued && string.Equals(notification.Queue, queue, StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }
}
