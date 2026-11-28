using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.Core.Services;
using Warp.Core.Webhooks;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// #344, rule 2.14: in an <c>AddWarp</c>-only process (dashboard-only or publisher-only, no server) the dashboard
/// query, command and admin services run on Warp's own context, not the application's. The application's context
/// carries a command interceptor; none of these calls may reach it. On PostgreSQL the application's context uses
/// snake_case naming, so the Warp context must resolve the mirrored names to work at all.
/// </summary>
[GenerateDatabaseTests]
public abstract class WarpOwnedContextTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WarpOwnedContextTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetJobsList_AddWarpOnlyHost_ReadsOnTheWarpContext()
    {
        var jobId = await SeedJobAsync(State.Failed);
        await using var host = BuildAddWarpOnlyHost();
        await using var scope = host.Services.CreateAsyncScope();

        var jobs = await scope.ServiceProvider.GetRequiredService<IJobQueryService>()
            .GetJobsList(new BaseListRequest { PageSize = 10 }, State.Failed);

        jobs.Items.ShouldHaveSingleItem().Id.ShouldBe(jobId);
        host.ApplicationCommands.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task RequeueJob_AddWarpOnlyHost_WritesOnTheWarpContext()
    {
        var jobId = await SeedJobAsync(State.Failed);
        await using var host = BuildAddWarpOnlyHost();
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IJobCommandService>().RequeueJob(jobId);

        var job = await _fixture.CreateContext().Set<Job>().AsNoTracking().SingleAsync(x => x.Id == jobId, Ct);
        job.CurrentState.ShouldBe(State.Enqueued);
        host.ApplicationCommands.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task ConcurrencyLimitManager_AddWarpOnlyHost_WritesOnTheWarpContext()
    {
        await using var host = BuildAddWarpOnlyHost();
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IConcurrencyLimitManager>().AddOrUpdateLimit("tenant-key", 3, Ct);

        var row = await _fixture.CreateContext().Set<ConcurrencyLimit>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("tenant-key");
        row.Limit.ShouldBe(3);
        host.ApplicationCommands.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task RateLimitManager_AddWarpOnlyHost_WritesOnTheWarpContext()
    {
        await using var host = BuildAddWarpOnlyHost();
        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IRateLimitManager>().AddOrUpdateLimit("api-key", 10, 60, Ct);

        var row = await _fixture.CreateContext().Set<RateLimitOverride>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("api-key");
        row.Count.ShouldBe(10);
        row.WindowSeconds.ShouldBe(60);
        host.ApplicationCommands.Count.ShouldBe(0);
    }

    // The flip and the executor job commit in one transaction on the Warp context: the job is staged there, not
    // through the application's IPublisher (another connection, which could commit the job without the flip).
    [TimedFact]
    public async Task Redeliver_AddWarpOnlyHost_StagesTheExecutorJobOnTheWarpContext()
    {
        var deliveryId = await SeedDeliveryAsync(WebhookDeliveryStatus.Delivered);
        await using var host = BuildAddWarpOnlyHost();
        await using var scope = host.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IWebhookCommandService>().Redeliver(deliveryId, Ct);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);
        await using var ctx = _fixture.CreateContext();
        var delivery = await ctx.Set<WebhookDelivery>().AsNoTracking().SingleAsync(x => x.Id == deliveryId, Ct);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        var job = await ctx.Set<Job>().AsNoTracking().SingleAsync(Ct);
        job.Queue.ShouldBe("default");
        job.CurrentState.ShouldBe(State.Enqueued);
        job.Message.ShouldNotBeNull().ShouldContain(deliveryId.ToString());
        host.ApplicationCommands.Count.ShouldBe(0);
    }

    private async Task<Guid> SeedJobAsync(State state)
    {
        await using var ctx = _fixture.CreateContext();
        var job = JobHelper.CreateJob(new StageChildOnQueueRequest(), DateTime.UtcNow, "default", parentId: null, state, DateTime.UtcNow);
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Ct);

        return job.Id;
    }

    private async Task<Guid> SeedDeliveryAsync(WebhookDeliveryStatus status)
    {
        await using var ctx = _fixture.CreateContext();
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{\"order\":42}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            AttemptCount = 3,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    // An AddWarp-only process: the provider, the two addon admin managers, no server. Only the service provider is
    // built: nothing here needs a hosted service.
    private AddWarpOnlyHost BuildAddWarpOnlyHost()
    {
        using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        var commands = new CommandRecorder();

        var services = new ServiceCollection();
        services.AddLogging(x => x.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<TestContext>(x =>
        {
            if (isPostgres)
            {
                x.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                x.UseSqlServer(connectionString);
            }

            x.AddInterceptors(commands);
        });
        services.AddWarp<TestContext>(x =>
        {
            if (isPostgres)
            {
                x.UsePostgreSql();
            }
            else
            {
                x.UseSqlServer();
            }

            x.AddConcurrency();
            x.AddRateLimit();
        });

        return new AddWarpOnlyHost(services.BuildServiceProvider(), commands);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
        {
            Interlocked.Increment(ref _count);

            return result;
        }
    }

    private sealed record AddWarpOnlyHost(ServiceProvider Services, CommandRecorder ApplicationCommands) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
