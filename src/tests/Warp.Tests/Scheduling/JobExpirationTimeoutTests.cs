using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Scheduling;

[GenerateDatabaseTests]
public abstract class JobExpirationTimeoutTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobExpirationTimeoutTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task DeleteJob_UsesConfiguredExpirationTimeout()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var config = new WarpConfiguration { JobExpirationTimeout = TimeSpan.FromHours(2) };
        var ctxForSvc = _fixture.CreateContext();
        var svc = new JobCommandService<TestContext>(
            ctxForSvc,
            TimeProvider.System,
            Options.Create(config),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.QueriesFor(ctxForSvc),
            Warp.Tests.Helpers.TestTasks.NullSignals);

        // Act
        var before = DateTime.UtcNow;
        await svc.DeleteJob(jobId);
        var after = DateTime.UtcNow;

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ExpireAt.ShouldNotBeNull();
        job.ExpireAt.Value.ShouldBeGreaterThanOrEqualTo(before.AddHours(2).AddSeconds(-1));
        job.ExpireAt.Value.ShouldBeLessThanOrEqualTo(after.AddHours(2).AddSeconds(1));
    }

    [TimedFact]
    public async Task DeleteJob_DefaultTimeout_ExpiresInOneDay()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());

        // Act
        var before = DateTime.UtcNow;
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ExpireAt.ShouldNotBeNull();
        job.ExpireAt.Value.ShouldBeGreaterThanOrEqualTo(before.AddDays(1).AddSeconds(-1));
        job.ExpireAt.Value.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddDays(1).AddSeconds(1));
    }
}
