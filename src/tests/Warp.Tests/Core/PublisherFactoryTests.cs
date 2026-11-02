using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

// Issue #315: a publisher bound to a caller-supplied context (IDbContextFactory), so the business
// write and the staged job commit in one SaveChanges. Resolved through the real AddWarp registration
// so the wiring (pipeline, transport, signals) is what production gets.
[GenerateDatabaseTests]
public abstract class PublisherFactoryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected PublisherFactoryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Create_BusinessRowAndJob_OneSaveCommitsBoth()
    {
        await using var provider = BuildProvider(new RecordingTransport());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();
        await using var db = _fixture.CreateContext();
        db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });

        var publisher = factory.Create(db);
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var assertCtx = _fixture.CreateContext();
        var job = await assertCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
        (await assertCtx.TestLogs.CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Create_PlainContextSave_CommitsStagedJob()
    {
        await using var provider = BuildProvider(new RecordingTransport());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();
        await using var db = _fixture.CreateContext();

        var jobId = await factory.Create(db).Enqueue(new UnitRequest());
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var assertCtx = _fixture.CreateContext();
        (await assertCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Create_UnsavedContext_JobNotCommitted()
    {
        await using var provider = BuildProvider(new RecordingTransport());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();

        Guid jobId;
        await using (var db = _fixture.CreateContext())
        {
            jobId = await factory.Create(db).Enqueue(new UnitRequest());
        }

        await using var assertCtx = _fixture.CreateContext();
        (await assertCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [TimedFact]
    public async Task Create_PublishPipelineBehavior_StampsMetadata()
    {
        await using var provider = BuildProvider(new RecordingTransport(), services =>
            services.AddTransient<IPublishPipelineBehavior<UnitRequest>, StampBehavior>());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();
        await using var db = _fixture.CreateContext();

        var publisher = factory.Create(db);
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var assertCtx = _fixture.CreateContext();
        var job = await assertCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        var metadata = MetadataSerializer.Deserialize(job!.Metadata)!;
        metadata[StampBehavior.Key].ShouldBe(StampBehavior.Value);
    }

    [TimedFact]
    public async Task Create_SaveChanges_DispatchesJobEnqueuedNotification()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();
        await using var db = _fixture.CreateContext();

        var publisher = factory.Create(db);
        await publisher.Enqueue(new UnitRequest(), "factory-queue");
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        transport.Published.ShouldContain((NotificationKind.JobEnqueued, (string?)"factory-queue"));
    }

    [TimedFact]
    public async Task CreateBatch_StartNew_OneSaveCommitsBusinessRowAndBatch()
    {
        var transport = new RecordingTransport();
        await using var provider = BuildProvider(transport);
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IPublisherFactory<TestContext>>();
        await using var db = _fixture.CreateContext();
        db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });

        var batchPublisher = factory.CreateBatch(db);
        var batchId = await batchPublisher.StartNew([new UnitRequest(), new UnitRequest()]);
        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var assertCtx = _fixture.CreateContext();
        var children = await assertCtx.Set<Job>()
            .Where(x => x.ParentJobId == batchId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        children.ShouldBe(2);
        (await assertCtx.TestLogs.CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(1);
        transport.Published.ShouldContain(x => x.Kind == NotificationKind.JobEnqueued);
    }

    private static ServiceProvider BuildProvider(RecordingTransport transport, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The Blazor shape: TContext registered through the factory. The tests hand the publisher
        // factory a fixture context on the real database, so this registration is never opened.
        services.AddDbContextFactory<TestContext>(o => o.UseInMemoryDatabase($"publisher-factory-{Guid.NewGuid():N}"));
        services.AddSingleton<IWarpNotificationTransport>(transport);
        services.AddWarp<TestContext>();
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class StampBehavior : IPublishPipelineBehavior<UnitRequest>
    {
        public const string Key = "factory-stamp";
        public const string Value = "stamped";

        public Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            context.Metadata[Key] = Value;

            return next();
        }
    }

    private sealed class RecordingTransport : IWarpNotificationTransport
    {
        public List<(NotificationKind Kind, string? Queue)> Published { get; } = [];

        public Task ListenerReady { get; } = Task.CompletedTask;

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Published.Add((kind, queue));

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }
}
