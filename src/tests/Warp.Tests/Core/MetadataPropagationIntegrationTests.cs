using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

[GenerateDatabaseTests]
public abstract class MetadataPropagationIntegrationTestsBase : IntegrationTestBase
{
    private static readonly int[] OneSecondDelay = [1];

    protected MetadataPropagationIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenHandlerThatWritesMetadata_WhenCompleted_ThenHandlerMetadataPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new MetadataWriterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed);

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = MetadataSerializer.Deserialize(job.Metadata);
        metadata.ShouldContainKey("HandlerWrote");
        metadata["HandlerWrote"].ShouldBe("from-handler");
    }

    [TimedFact]
    public async Task GivenPublishPipelineAndHandler_WhenCompleted_ThenBothMetadataKeysPersisted()
    {
        // RetryPublishBehavior sets MaxRetries at publish time
        // Handler sets HandlerWrote at execution time
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new MetadataWriterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed);

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = MetadataSerializer.Deserialize(job.Metadata);

        // From RetryPublishBehavior (publish time)
        metadata.ShouldContainKey("MaxRetries");

        // From handler (execution time)
        metadata.ShouldContainKey("HandlerWrote");
        metadata["HandlerWrote"].ShouldBe("from-handler");
    }

    [TimedFact]
    public async Task GivenUserMetadataAtPublishTime_WhenCompleted_ThenUserMetadataPreserved()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new MetadataWriterRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["UserKey"] = "user-value" },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed);

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = MetadataSerializer.Deserialize(job.Metadata);

        // User-set at publish time
        metadata.ShouldContainKey("UserKey");
        metadata["UserKey"].ShouldBe("user-value");

        // Addon-set at publish time (RetryPublishBehavior)
        metadata.ShouldContainKey("MaxRetries");

        // Handler-set at execution time
        metadata.ShouldContainKey("HandlerWrote");
    }

    [TimedFact]
    public async Task GivenFailingJobWithRetry_WhenFailed_ThenRetryMetadataPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object>
            {
                ["MaxRetries"] = 1,
                ["RetryDelays"] = OneSecondDelay,
            },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        // Retry behavior wrote RetriedTimes to metadata during failure handling
        var metadata = MetadataSerializer.Deserialize(job.Metadata);
        metadata.ShouldContainKey("RetriedTimes");
        Convert.ToInt32(metadata["RetriedTimes"]).ShouldBe(1);
    }

    [TimedFact]
    public async Task GivenChildJobSpawnedByHandler_WhenCompleted_ThenChildDoesNotInheritParentMetadata()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new SpawnChildJobRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["ParentKey"] = "not-inherited" },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();
        var children = await ctx.Set<Job>()
            .Where(x => x.SpawnedByJobId == jobId)
            .Where(x => x.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        // The SpawnedByJobId link still connects child to parent for trace correlation.
        children.Count.ShouldBeGreaterThan(0);

        foreach (var child in children)
        {
            // Metadata is not inherited — the parent's key must not leak onto the child.
            var metadata = MetadataSerializer.Deserialize(child.Metadata);
            metadata?.ShouldNotContainKey("ParentKey");
        }
    }
}
