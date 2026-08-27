using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

[GenerateDatabaseTests]
public abstract class ScopeIsolationIntegrationTestsBase : IntegrationTestBase
{
    private static readonly int[] OneSecondDelay = [1];

    protected ScopeIsolationIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenHandlerThatAddsEntityAndThrows_WhenProcessed_ThenEntityNotPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new AddEntityThenThrowRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["MaxRetries"] = 0 },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed);

        var ctx = Fixture.CreateContext();
        var leaked = await ctx.Set<Counter>()
            .Where(x => x.Key == "handler-leaked-entity")
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);
        leaked.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GivenHandlerThatAddsEntitySavesAndThrows_WhenProcessed_ThenEntityPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new AddEntitySaveThenThrowRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["MaxRetries"] = 0 },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed);

        var ctx = Fixture.CreateContext();
        var committed = await ctx.Set<Counter>()
            .Where(x => x.Key == "handler-committed-entity")
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);
        committed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task GivenHandlerThatAddsEntityAndThrows_WithRetry_ThenEntityNotPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new AddEntityThenThrowRequest(), new JobParameters
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
        var leaked = await ctx.Set<Counter>()
            .Where(x => x.Key == "handler-leaked-entity")
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);
        leaked.ShouldBeFalse();

        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
    }

    [TimedFact]
    public async Task GivenSuccessfulHandler_WhenChildJobPublished_ThenChildJobPersisted()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new SpawnChildJobRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();
        var childJobs = await ctx.Set<Job>()
            .Where(x => x.SpawnedByJobId == jobId)
            .Where(x => x.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        childJobs.Count.ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task GivenSuccessfulHandler_WhenMetadataModifiedByPipeline_ThenMetadataPersisted()
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
        job.CurrentState.ShouldBe(State.Completed);

        // Metadata should be persisted even on success. MetadataWriterRequest carries [Retry(3)], so
        // PolicyResolver stamps MaxRetries during the attempt and the finalizing write persists it (§8.8).
        // The global default is never stamped (#236).
        job.Metadata.ShouldNotBeNull();
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("MaxRetries");
    }
}
