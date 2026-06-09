using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class ProgressReportingDispatcherIntegrationTestsBase : IntegrationTestBase
{
    protected ProgressReportingDispatcherIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static void ConfigureDispatcher(WarpServerBuilder<TestContext> config)
    {
        config.UseDispatcher = true;
        config.WorkerCount = 2;
        config.CompletionBatchSize = 10;
        config.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
    }

    [TimedFact]
    public async Task GivenDispatcherMode_WhenHandlerReportsProgress_ThenProgressRowsArePersistedViaBatchedCompletion()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureDispatcher);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new MultiBarProgressRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion(TimeSpan.FromSeconds(20));

        var ctx = Fixture.CreateContext();
        var progressRows = await ctx.Set<JobLog>()
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).ShouldBe(["download", "process", "upload"], ignoreOrder: true);
        progressRows.Single(x => string.Equals(x.Name, "download", StringComparison.Ordinal)).Value.ShouldBe((short)100);
        progressRows.Single(x => string.Equals(x.Name, "process", StringComparison.Ordinal)).Value.ShouldBe((short)50);
        progressRows.Single(x => string.Equals(x.Name, "upload", StringComparison.Ordinal)).Value.ShouldBe((short)10);
    }
}
