using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Diagnostics;
using Warp.Core.Enums;
using Warp.Tests.Adapters;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 3 coverage for the multi-application observability registry: the non-server
/// <see cref="ApplicationHeartbeatHost{TContext}"/> register/deregister lifecycle, the
/// <c>ExpirationCleanup</c> stale-instance + log-retention sweeps, and the server-side
/// <see cref="WarpServerRegistration{TContext}"/> stamping <c>Server.Application/Version/Environment</c>.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class ApplicationRegistryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ApplicationRegistryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Host_StartAsync_RegistersInstanceAndRegisteredLog()
    {
        var host = CreateHost("publisher", version: "1.2.3", environment: "test");

        await host.StartAsync(Ct);

        try
        {
            var instance = await _fixture.CreateContext().Set<ApplicationInstance>().SingleAsync(Ct);
            instance.ApplicationName.ShouldBe("publisher");
            instance.Version.ShouldBe("1.2.3");
            instance.Environment.ShouldBe("test");
            instance.MachineName.ShouldNotBeNullOrEmpty();

            var log = await _fixture.CreateContext().Set<ApplicationInstanceLog>().SingleAsync(Ct);
            log.EventType.ShouldBe(ApplicationInstanceEventType.Registered);
            log.InstanceId.ShouldBe(instance.Id);
            log.ApplicationName.ShouldBe("publisher");
            log.ExpireAt.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task Host_StartAsync_ServerProcess_StaysInert()
    {
        // A server process carries IWarpServerPresence — the host must NOT write an ApplicationInstance
        // (the server records itself on its Server row instead).
        var host = CreateHost("api", serverPresent: true);

        await host.StartAsync(Ct);

        try
        {
            (await _fixture.CreateContext().Set<ApplicationInstance>().AnyAsync(Ct)).ShouldBeFalse();
            (await _fixture.CreateContext().Set<ApplicationInstanceLog>().AnyAsync(Ct)).ShouldBeFalse();
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task Host_StopAsync_DeregistersInstanceAndWritesStoppedLog()
    {
        var host = CreateHost("worker-tools");

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);

        (await _fixture.CreateContext().Set<ApplicationInstance>().AnyAsync(Ct)).ShouldBeFalse();

        var events = await _fixture.CreateContext().Set<ApplicationInstanceLog>()
            .OrderBy(x => x.Timestamp)
            .Select(x => x.EventType)
            .ToListAsync(Ct);

        events.ShouldContain(ApplicationInstanceEventType.Registered);
        events.ShouldContain(ApplicationInstanceEventType.Stopped);
    }

    [TimedFact]
    public async Task Heartbeat_PeriodicTick_AdvancesLastHeartbeatAndRefreshesCpuRam()
    {
        // Drives the exact body the periodic loop runs (HeartbeatAsync) with a controlled clock, so the
        // LastHeartbeatAt advance + CPU/RAM refresh are deterministic (no wall-clock poll / flake). The
        // interval is set long so the real background loop stays dormant while the tick is driven directly.
        var start = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var host = CreateHost("publisher", timeProvider: time, heartbeatInterval: TimeSpan.FromHours(1));

        await host.StartAsync(Ct);
        try
        {
            var registered = await _fixture.CreateContext().Set<ApplicationInstance>().SingleAsync(Ct);
            registered.LastHeartbeatAt.ShouldBe(start.UtcDateTime);

            // Advance the clock, then drive one heartbeat tick.
            time.Advance(TimeSpan.FromMinutes(5));
            await host.HeartbeatAsync(Ct);

            var refreshed = await _fixture.CreateContext().Set<ApplicationInstance>().SingleAsync(Ct);
            refreshed.LastHeartbeatAt.ShouldBeGreaterThan(registered.LastHeartbeatAt);
            refreshed.MemoryWorkingSetBytes.ShouldNotBeNull();
            refreshed.CpuUsagePercent.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task Heartbeat_InstanceSweptWhileAlive_WarnsExactlyOnce()
    {
        // Exercises the warn-once stale-swept branch: the row is deleted out from under a running host, then
        // two ticks are driven — the latch must fire the warning exactly once and never recreate the row.
        var logger = new CapturingLogger<ApplicationHeartbeatHost<TestContext>>();
        var host = CreateHost("publisher", logger: logger, heartbeatInterval: TimeSpan.FromHours(1));

        await host.StartAsync(Ct);
        try
        {
            await _fixture.CreateContext().Set<ApplicationInstance>().ExecuteDeleteAsync(Ct);

            await host.HeartbeatAsync(Ct);
            await host.HeartbeatAsync(Ct);

            logger.WarningCount.ShouldBe(1);

            // The row is deliberately NOT recreated by a tick against a swept instance.
            (await _fixture.CreateContext().Set<ApplicationInstance>().AnyAsync(Ct)).ShouldBeFalse();
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task Cleanup_StaleInstance_DeletedWithStaleSweptLog()
    {
        await InsertInstanceAsync("dead-publisher", lastHeartbeatAt: DateTime.UtcNow.AddMinutes(-10));

        var swept = await CreateCleanup(staleGrace: TimeSpan.FromMinutes(2))
            .CleanupStaleApplicationInstancesAsync(Ct);

        swept.ShouldBe(1);
        (await _fixture.CreateContext().Set<ApplicationInstance>().AnyAsync(Ct)).ShouldBeFalse();

        var log = await _fixture.CreateContext().Set<ApplicationInstanceLog>().SingleAsync(Ct);
        log.EventType.ShouldBe(ApplicationInstanceEventType.StaleSwept);
        log.ApplicationName.ShouldBe("dead-publisher");
    }

    [TimedFact]
    public async Task Cleanup_FreshInstance_WithinGrace_Kept()
    {
        await InsertInstanceAsync("live-publisher", lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-5));

        var swept = await CreateCleanup(staleGrace: TimeSpan.FromMinutes(2))
            .CleanupStaleApplicationInstancesAsync(Ct);

        swept.ShouldBe(0);
        (await _fixture.CreateContext().Set<ApplicationInstance>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Cleanup_ExpiredLog_Deleted_UnexpiredKept()
    {
        await InsertLogAsync("app", ApplicationInstanceEventType.Registered, expireAt: DateTime.UtcNow.AddHours(-1));
        await InsertLogAsync("app", ApplicationInstanceEventType.Stopped, expireAt: DateTime.UtcNow.AddHours(1));

        var deleted = await CreateCleanup().CleanupExpiredApplicationInstanceLogsAsync(Ct);

        deleted.ShouldBe(1);
        var remaining = await _fixture.CreateContext().Set<ApplicationInstanceLog>()
            .Select(x => x.EventType)
            .ToListAsync(Ct);
        remaining.ShouldHaveSingleItem().ShouldBe(ApplicationInstanceEventType.Stopped);
    }

    [TimedFact]
    public async Task CleanupByCount_KeepsNewestNPerInstance()
    {
        var instanceId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await InsertLogAsync("app", ApplicationInstanceEventType.Registered, expireAt: null, instanceId: instanceId, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        var deleted = await CreateCleanup(logRetentionCount: 2).CleanupApplicationInstanceLogsByCountAsync(Ct);

        deleted.ShouldBe(3);
        (await _fixture.CreateContext().Set<ApplicationInstanceLog>().CountAsync(Ct)).ShouldBe(2);
    }

    [TimedFact]
    public async Task ServerRegistration_StampsApplicationVersionEnvironment_AndRegisteredLog()
    {
        var serverId = Guid.NewGuid();
        var registration = CreateServerRegistration(serverId, "api", "2.0.0", "prod");

        await registration.StartAsync(Ct);

        var server = await _fixture.CreateContext().Set<Server>().SingleAsync(x => x.Id == serverId, Ct);
        server.Application.ShouldBe("api");
        server.Version.ShouldBe("2.0.0");
        server.Environment.ShouldBe("prod");

        var log = await _fixture.CreateContext().Set<ApplicationInstanceLog>().SingleAsync(Ct);
        log.EventType.ShouldBe(ApplicationInstanceEventType.Registered);
        log.InstanceId.ShouldBe(serverId);
        log.ApplicationName.ShouldBe("api");
    }

    [TimedFact]
    public async Task ServerRegistration_WithoutApplicationName_WritesNoLifecycleLog()
    {
        var serverId = Guid.NewGuid();
        var registration = CreateServerRegistration(serverId, application: null, version: null, environment: null);

        await registration.StartAsync(Ct);

        var server = await _fixture.CreateContext().Set<Server>().SingleAsync(x => x.Id == serverId, Ct);
        server.Application.ShouldBeNull();
        (await _fixture.CreateContext().Set<ApplicationInstanceLog>().AnyAsync(Ct)).ShouldBeFalse();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ApplicationHeartbeatHost<TestContext> CreateHost(
        string applicationName,
        string? version = null,
        string? environment = null,
        bool serverPresent = false,
        TimeProvider? timeProvider = null,
        ILogger<ApplicationHeartbeatHost<TestContext>>? logger = null,
        TimeSpan? heartbeatInterval = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var time = timeProvider ?? TimeProvider.System;

        var config = new WarpConfiguration
        {
            ApplicationName = applicationName,
            ApplicationVersion = version,
            ApplicationEnvironment = environment,
        };

        if (heartbeatInterval is { } interval)
        {
            config.ApplicationHeartbeatInterval = interval;
        }

        IEnumerable<IWarpServerPresence> presences = serverPresent
            ? [new StubServerPresence()]
            : [];

        return new ApplicationHeartbeatHost<TestContext>(
            scopeFactory,
            Options.Create(config),
            time,
            new ProcessCpuTracker(time),
            logger ?? NullLogger<ApplicationHeartbeatHost<TestContext>>.Instance,
            presences);
    }

    private ExpirationCleanup<TestContext> CreateCleanup(
        TimeSpan? staleGrace = null,
        int? logRetentionCount = null)
    {
        var configuration = new WarpServerConfiguration
        {
            ApplicationInstanceStaleGrace = staleGrace ?? TimeSpan.FromMinutes(2),
            ApplicationInstanceLogRetentionCount = logRetentionCount,
        };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration),
            TestNotifiers.EmptyDispatcher());
    }

    private WarpServerRegistration<TestContext> CreateServerRegistration(
        Guid serverId,
        string? application,
        string? version,
        string? environment)
    {
        var configuration = new WarpServerConfiguration
        {
            ServerId = serverId,
            WorkerCount = 1,
            ApplicationName = application,
            ApplicationVersion = version,
            ApplicationEnvironment = environment,
        };

        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new WarpServerRegistration<TestContext>(
            Options.Create(configuration),
            Options.Create<WarpConfiguration>(configuration),
            scopeFactory,
            TimeProvider.System,
            new PauseStateHolder(),
            new ServerRegistrationState());
    }

    private async Task InsertInstanceAsync(string applicationName, DateTime lastHeartbeatAt)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = Guid.NewGuid(),
            ApplicationName = applicationName,
            MachineName = "test-host",
            StartedAt = lastHeartbeatAt,
            LastHeartbeatAt = lastHeartbeatAt,
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private async Task InsertLogAsync(
        string applicationName,
        ApplicationInstanceEventType eventType,
        DateTime? expireAt,
        Guid? instanceId = null,
        DateTime? timestamp = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstanceLog>().Add(new ApplicationInstanceLog
        {
            InstanceId = instanceId ?? Guid.NewGuid(),
            ApplicationName = applicationName,
            Timestamp = timestamp ?? DateTime.UtcNow,
            EventType = eventType,
            ExpireAt = expireAt,
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private sealed class StubServerPresence : IWarpServerPresence;
}
