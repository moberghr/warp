using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Notifications;

// Issue #322: jobs committed by a plain DbContext.SaveChangesAsync (not IPublisher.SaveChangesAsync)
// never woke a worker — neither the cross-process NOTIFY nor the in-process signal. The interceptor
// AddWarp puts on TContext now announces them, and holds the wake-up until an explicit transaction
// commits. Resolved through the real AddWarp registration so the options wiring is what ships.
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
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        transport.ForQueue(Queue).ShouldBe(1);
        local.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChanges_Sync_AfterEnqueue_SendsWakeUpAndSignalsLocally()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);

        // The synchronous path is what this test covers.
#pragma warning disable MA0042, S6966
        db.SaveChanges();
#pragma warning restore MA0042, S6966

        // The sync path does not await the transport; this recording transport completes
        // synchronously, so the publish has already happened when SaveChanges returns.
        local.Count.ShouldBe(1);
        transport.ForQueue(Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task PublisherSaveChangesAsync_SendsExactlyOneWakeUp()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var local = CountLocalSignals(provider);

        await publisher.Enqueue(new UnitRequest(), Queue);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        transport.ForQueue(Queue).ShouldBe(1);
        local.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_InsideExplicitTransaction_WaitsForCommit()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(ct);

        transport.ForQueue(Queue).ShouldBe(0);

        await tx.CommitAsync(ct);

        transport.ForQueue(Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_TwoSavesInOneTransaction_SendOneWakeUpOnCommit()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
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

        transport.ForQueue(Queue).ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveChangesAsync_InsideRolledBackTransaction_SendsNothing()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
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

        transport.ForQueue(Queue).ShouldBe(0);
        local.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task SaveChangesAsync_TransactionDisposedWithoutCommit_DoesNotLeakIntoTheNextOne()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
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

        transport.ForQueue(Queue).ShouldBe(0);
    }

    [TimedFact]
    public async Task SaveChangesAsync_WithoutStagedJobs_SendsNothing()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();
        var local = CountLocalSignals(provider);

        db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        transport.Published.ShouldBeEmpty();
        local.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task AddWarp_CalledTwice_RegistersTheInterceptorOnce()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport, services => services.AddWarp<TestContext>());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestContext>();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest(), Queue);
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        transport.ForQueue(Queue).ShouldBe(1);
    }

    private static LocalSignalCounter CountLocalSignals(ServiceProvider provider)
    {
        var counter = new LocalSignalCounter();
        provider.GetRequiredService<ServerTaskSignals<TestContext>>().Subscribe(ServerTaskSignal.JobEnqueued, counter.Increment);

        return counter;
    }

    private ServiceProvider BuildProvider(RecordingTransport transport, Action<IServiceCollection>? configure = null)
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
        services.AddSingleton<IWarpNotificationTransport>(transport);
        services.AddWarp<TestContext>();
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class LocalSignalCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class RecordingTransport : IWarpNotificationTransport
    {
        public ConcurrentQueue<(NotificationKind Kind, string? Queue)> Published { get; } = new();

        public Task ListenerReady { get; } = Task.CompletedTask;

        public int ForQueue(string queue) => Published.Count(x => x.Kind == NotificationKind.JobEnqueued && string.Equals(x.Queue, queue, StringComparison.Ordinal));

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Published.Enqueue((kind, queue));

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }
}
