using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class LogRetentionTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string ServiceName = "RetentionTestService";

    protected LogRetentionTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedServerAsync(ServerId, "retention-test-server");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ExpirationCleanup<TestContext> CreateCleanup(
        int retentionCount = 1000,
        TimeSpan? retentionAge = null,
        TimeProvider? time = null,
        IEnumerable<WarpBackgroundService>? backgroundServices = null)
    {
        var config = new WarpServerConfiguration
        {
            BackgroundServiceLogRetentionCount = retentionCount,
            BackgroundServiceLogRetentionAge = retentionAge ?? TimeSpan.FromDays(7),
        };

        return new ExpirationCleanup<TestContext>(
            _fixture.CreateContext(),
            time ?? TimeProvider.System,
            Options.Create(config),
            backgroundServices);
    }

    private async Task InsertInstanceWithLogsAsync(int logCount, DateTime? baseTimestamp = null)
    {
        var ctx = _fixture.CreateContext();

        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = ServerId,
            ServiceName = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var logCtx = _fixture.CreateContext();
        var ts = baseTimestamp ?? DateTime.UtcNow;

        for (var i = 0; i < logCount; i++)
        {
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = ServerId,
                ServiceName = ServiceName,
                Timestamp = ts.AddMinutes(-logCount + i),
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.User,
                Message = $"log entry {i}",
            });
        }

        await logCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [TimedFact]
    public async Task ExpirationCleanup_InstanceWith1500Logs_Keeps1000NewestDeletesRest()
    {
        await InsertInstanceWithLogsAsync(1500);

        var cleanup = CreateCleanup(retentionCount: 1000);
        await cleanup.CleanupBackgroundServiceLogsAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var remaining = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServerId == ServerId)
            .Where(l => l.ServiceName == ServiceName)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldBe(1000);
    }

    [TimedFact]
    public async Task ExpirationCleanup_InstanceBelowRetentionCount_NoRowsDeleted()
    {
        await InsertInstanceWithLogsAsync(500);

        var cleanup = CreateCleanup(retentionCount: 1000);
        await cleanup.CleanupBackgroundServiceLogsAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var remaining = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServerId == ServerId)
            .Where(l => l.ServiceName == ServiceName)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldBe(500);
    }

    [TimedFact]
    public async Task ExpirationCleanup_LogsOlderThan7Days_Deleted()
    {
        // Insert 5 old logs (8 days ago) and 5 recent logs.
        var ctx = _fixture.CreateContext();

        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = ServerId,
            ServiceName = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var logCtx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = ServerId,
                ServiceName = ServiceName,
                Timestamp = now.AddDays(-8),
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.User,
                Message = $"old log {i}",
            });
        }

        for (var i = 0; i < 5; i++)
        {
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = ServerId,
                ServiceName = ServiceName,
                Timestamp = now.AddHours(-1),
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.User,
                Message = $"recent log {i}",
            });
        }

        await logCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var fakeNow = new FakeTimeProvider(now);
        var cleanup = CreateCleanup(retentionAge: TimeSpan.FromDays(7), time: fakeNow);
        await cleanup.CleanupBackgroundServiceLogsAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var remaining = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServerId == ServerId)
            .Where(l => l.ServiceName == ServiceName)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldBe(5, "Only the 5 recent logs should survive; the 5 old ones should be deleted");
    }

    [TimedFact]
    public async Task ExpirationCleanup_LogsCascadeWhenInstanceDeleted()
    {
        // Insert an instance with 5 logs, then delete the instance row and verify cascade.
        var ctx = _fixture.CreateContext();

        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = ServerId,
            ServiceName = ServiceName,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var logCtx = _fixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = ServerId,
                ServiceName = ServiceName,
                Timestamp = DateTime.UtcNow,
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.Lifecycle,
                Message = $"log {i}",
            });
        }

        await logCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Verify logs exist before delete.
        var beforeCtx = _fixture.CreateContext();
        var logsBefore = await beforeCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServerId == ServerId)
            .Where(l => l.ServiceName == ServiceName)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        logsBefore.ShouldBe(5);

        // Delete the instance row — this should cascade-delete the logs.
        var deleteCtx = _fixture.CreateContext();
        await deleteCtx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == ServerId)
            .Where(x => x.ServiceName == ServiceName)
            .ExecuteDeleteAsync(Xunit.TestContext.Current.CancellationToken);

        // Verify logs are gone.
        var readCtx = _fixture.CreateContext();
        var logsAfter = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServerId == ServerId)
            .Where(l => l.ServiceName == ServiceName)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        logsAfter.ShouldBe(0, "Logs should be cascade-deleted with their instance row");
    }

    [TimedFact]
    public async Task ExpirationCleanup_ServiceWithLowerCountOverride_RetainsFewerRows()
    {
        // Arrange: two services — one with a low count override (5), one using the global (1000).
        const string overrideSvc = "LowRetentionService";
        const string defaultSvc = "DefaultRetentionService";
        var overrideServerId = Guid.NewGuid();
        var defaultServerId = Guid.NewGuid();

        await _fixture.SeedServerAsync(overrideServerId, "override-server", Xunit.TestContext.Current.CancellationToken);
        await _fixture.SeedServerAsync(defaultServerId, "default-server", Xunit.TestContext.Current.CancellationToken);

        var setupCtx = _fixture.CreateContext();
        setupCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = overrideSvc,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        setupCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = defaultSvc,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        setupCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = overrideServerId,
            ServiceName = overrideSvc,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        setupCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = defaultServerId,
            ServiceName = defaultSvc,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        await setupCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var logCtx = _fixture.CreateContext();
        for (var i = 0; i < 20; i++)
        {
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = overrideServerId,
                ServiceName = overrideSvc,
                Timestamp = DateTime.UtcNow.AddMinutes(-20 + i),
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.User,
                Message = $"override log {i}",
            });
            logCtx.Set<BackgroundServiceLog>().Add(new BackgroundServiceLog
            {
                ServerId = defaultServerId,
                ServiceName = defaultSvc,
                Timestamp = DateTime.UtcNow.AddMinutes(-20 + i),
                Level = Microsoft.Extensions.Logging.LogLevel.Information,
                Source = BackgroundServiceLogSource.User,
                Message = $"default log {i}",
            });
        }

        await logCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act: run cleanup with overrideSvc having a count override of 5, global = 1000.
        var overrideService = new LowRetentionService();
        var cleanup = CreateCleanup(retentionCount: 1000, backgroundServices: [overrideService]);
        await cleanup.CleanupBackgroundServiceLogsAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: overrideSvc should have 5 rows; defaultSvc should still have all 20 (below global threshold).
        var readCtx = _fixture.CreateContext();

        var overrideRemaining = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServiceName == overrideSvc)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        var defaultRemaining = await readCtx.Set<BackgroundServiceLog>()
            .Where(l => l.ServiceName == defaultSvc)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        overrideRemaining.ShouldBe(5, "LowRetentionService.LogRetentionCountOverride = 5 must be honoured");
        defaultRemaining.ShouldBe(20, "DefaultSvc uses global threshold of 1000 and has only 20 rows; none should be deleted");
    }
}

/// <summary>
/// Test service with a low <see cref="WarpBackgroundService.LogRetentionCountOverride"/>.
/// Used to verify that per-service overrides are applied by <c>ExpirationCleanup</c>.
/// </summary>
file sealed class LowRetentionService : WarpBackgroundService
{
    public override string Name => "LowRetentionService";

    public override int? LogRetentionCountOverride => 5;

    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
}

file class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
