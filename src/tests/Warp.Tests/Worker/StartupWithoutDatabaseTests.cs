using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Tests.Observability;
using Warp.Worker;

namespace Warp.Tests.Worker;

/// <summary>
/// <see cref="WarpConfiguration.RequireDatabaseAtStartup"/>. A full server host (one dispatcher worker, every server
/// loop) on an in-memory server context whose saves fail while <see cref="DatabaseSwitch.Down"/> is set: the
/// registration's first write is the first thing that reaches the database. The worker's claim and the heartbeat
/// go through the mocked <see cref="IWarpSqlQueries{TContext}"/>, so its invocations show whether anything ran.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class StartupWithoutDatabaseTests
{
    [TimedFact]
    public async Task StartAsync_DefaultSettingDatabaseUnreachable_HostRefusesToStart()
    {
        using var server = new TestServer(requireDatabase: true);

        await Should.ThrowAsync<DatabaseDownException>(() => server.Host.StartAsync(Xunit.TestContext.Current.CancellationToken));
    }

    [TimedFact]
    public async Task StartAsync_OptedOutDatabaseUnreachable_HostStartsWithOneWarningAndRunsNothing()
    {
        using var server = new TestServer(requireDatabase: false);

        await server.Host.StartAsync(Xunit.TestContext.Current.CancellationToken);

        server.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
        server.Logs.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("could not reach the database at startup", StringComparison.Ordinal));
        server.Logs.ShouldNotContain(x => x.Level >= LogLevel.Error);
        server.Gate.IsOpen.ShouldBeFalse();
        server.Host.Services.GetRequiredService<ServerRegistrationState>().Groups.ShouldBeEmpty();
        server.Queries.Invocations.ShouldBeEmpty();
        await server.Host.StopAsync(CancellationToken.None);
    }

    [TimedFact]
    public async Task StartAsync_OptedOutDatabaseBecomesReachable_RegistersAndStartsTheWorker()
    {
        using var server = new TestServer(requireDatabase: false);
        await server.Host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        (await server.RetryWaiting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();

        server.Database.Down = false;
        server.Time.Advance(WarpStartupGate.InitialRetryDelay);

        await server.Gate.WaitUntilOpenAsync(Xunit.TestContext.Current.CancellationToken);
        await server.Claimed.Task.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Registered: the worker groups are populated.
        server.Host.Services.GetRequiredService<ServerRegistrationState>().Groups.Count.ShouldBe(1);
        await using (var scope = server.Host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            (await context.Set<Server>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        // Not stopped: a registered server deregisters with ExecuteDelete, which the in-memory provider lacks
        // (deregistration is covered on both databases by WarpServerRegistrationTests). Disposing ends the loops.
    }

    [TimedFact]
    public async Task StopAsync_OptedOutStillWaitingForTheDatabase_StopsWithoutTouchingIt()
    {
        using var server = new TestServer(requireDatabase: false);
        await server.Host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        (await server.RetryWaiting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();
        var savesBeforeStop = server.Database.Saves;

        await server.Host.StopAsync(Xunit.TestContext.Current.CancellationToken);

        server.Database.Saves.ShouldBe(savesBeforeStop);
        server.Queries.Invocations.ShouldBeEmpty();
        server.Gate.IsOpen.ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    public async Task StartAsync_RealProviderDatabaseHostDoesNotResolve_DefaultFailsAndOptOutStarts(string provider)
    {
        // The real providers against a host name that cannot resolve: what an application's controller tests see
        // when Program.cs points at a database that is not there.
        using var required = RealProviderHost(provider, requireDatabase: true, out _);
        await Should.ThrowAsync<Exception>(() => required.StartAsync(Xunit.TestContext.Current.CancellationToken));

        using var optedOut = RealProviderHost(provider, requireDatabase: false, out var logs);
        await optedOut.StartAsync(Xunit.TestContext.Current.CancellationToken);
        await optedOut.StopAsync(Xunit.TestContext.Current.CancellationToken);

        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
        logs.Logs.Where(x => x.Level >= LogLevel.Error).Select(x => $"{x.Category}: {x.Message}").ShouldBeEmpty();
    }

    [TimedFact]
    public async Task SloSeederStartAsync_ServerNotRegisteredYet_SeedsOnlyOnceItIs()
    {
        using var gate = NewGate(requireDatabase: false, TimeProvider.System);
        var scopeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(x => x.CreateScope()).Callback(() => scopeRequested.TrySetResult()).Throws(new InvalidOperationException("no database in this test"));
        var seeder = new Warp.Core.Slo.SloSeeder<TestContext>(
            scopes.Object,
            new Warp.Core.Slo.SloOptions(),
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new WarpConfiguration()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Warp.Core.Slo.SloSeeder<TestContext>>.Instance,
            gate);

        await seeder.StartAsync(Xunit.TestContext.Current.CancellationToken);

        scopeRequested.Task.IsCompleted.ShouldBeFalse();
        gate.Open();
        await scopeRequested.Task.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await seeder.StopAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [TimedFact]
    public async Task RunAfterChecksAsync_ACheckIsRetrying_WaitsForItThenRuns()
    {
        var time = new FakeTimeProvider();
        using var gate = NewGate(requireDatabase: false, time);
        var checkReachable = false;
        var registrations = 0;
        await gate.RunCheckAsync("check", _ => checkReachable ? Task.CompletedTask : throw new DatabaseDownException(), NullLogger, Xunit.TestContext.Current.CancellationToken);

        var ranNow = await gate.RunAfterChecksAsync("registration", _ => Task.FromResult(++registrations), NullLogger, Xunit.TestContext.Current.CancellationToken);

        ranNow.ShouldBeFalse();
        registrations.ShouldBe(0);
        (await gate.RetryWaiting!.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();
        checkReachable = true;
        time.Advance(WarpStartupGate.InitialRetryDelay);
        (await gate.RetryWaiting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();
        registrations.ShouldBe(0);
        time.Advance(WarpStartupGate.InitialRetryDelay);
        await gate.StopAsync();
        registrations.ShouldBe(1);
    }

    [TimedFact]
    public async Task StopAsync_RegistrationWaitingBehindARetryingCheck_LogsNoError()
    {
        var logs = new CapturingLoggerProvider();
        var logger = logs.CreateLogger("startup");
        using var gate = NewGate(requireDatabase: false, new FakeTimeProvider());
        await gate.RunCheckAsync("check", _ => throw new DatabaseDownException(), logger, Xunit.TestContext.Current.CancellationToken);
        await gate.RunAfterChecksAsync("registration", _ => Task.CompletedTask, logger, Xunit.TestContext.Current.CancellationToken);
        (await gate.RetryWaiting!.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();

        await gate.StopAsync();

        logs.Logs.Where(x => x.Level >= LogLevel.Error).Select(x => x.Message).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task RunCheckAsync_OptedOutButTheFailureIsNotAboutReachingTheDatabase_Throws()
    {
        using var gate = NewGate(requireDatabase: false, TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            gate.RunCheckAsync("check", _ => throw new InvalidOperationException("server too old"), NullLogger, Xunit.TestContext.Current.CancellationToken));
    }

    [TimedTheory]
    [InlineData(null, false)]
    [InlineData("08006", true)]
    [InlineData("57P03", true)]
    [InlineData("53300", true)]
    [InlineData("3D000", true)]
    [InlineData("28P01", false)]
    [InlineData("42P01", false)]
    public void IsDatabaseUnavailable_ProviderException_DecidedBySqlState(string? sqlState, bool expected)
    {
        WarpStartupGate.IsUnreachableByDefault(new InvalidOperationException("wrapped", new DatabaseDownException { State = sqlState })).ShouldBe(expected);
    }

    [TimedFact]
    public void IsDatabaseUnavailable_SocketOrTimeoutAnywhereInTheChain_IsTrueAndAnythingElseFalse()
    {
        WarpStartupGate.IsUnreachableByDefault(new InvalidOperationException("wrapped", new SocketException())).ShouldBeTrue();
        WarpStartupGate.IsUnreachableByDefault(new TimeoutException()).ShouldBeTrue();

        // SqlClient on Linux (managed SNI): a refused port is a SqlException (number 35, no SQLSTATE, not transient)
        // around a SocketException whose error code reads Success. The code is not what decides.
        WarpStartupGate.IsUnreachableByDefault(new DatabaseDownException("refused", new SocketException((int)SocketError.Success)) { State = null }).ShouldBeTrue();
        WarpStartupGate.IsUnreachableByDefault(new InvalidOperationException("not the database")).ShouldBeFalse();
    }

    private static ILogger NullLogger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static IHost RealProviderHost(string provider, bool requireDatabase, out CapturingLoggerProvider logs)
    {
        var captured = new CapturingLoggerProvider();
        logs = captured;
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging(x => x.AddProvider(captured));
        if (string.Equals(provider, "PostgreSql", StringComparison.Ordinal))
        {
            builder.Services.AddDbContext<TestContext>(x => x.UseNpgsql("Host=warp-missing-database.invalid;Database=app;Timeout=2"));
            builder.Services.AddWarpServer<TestContext>(x =>
            {
                x.RequireDatabaseAtStartup = requireDatabase;
                Warp.Provider.PostgreSql.PostgreSqlServiceConfiguration.UsePostgreSql(x);
            });
        }
        else
        {
            builder.Services.AddDbContext<TestContext>(x => x.UseSqlServer("Server=warp-missing-database.invalid;Database=app;Connect Timeout=2;ConnectRetryCount=0;TrustServerCertificate=true"));
            builder.Services.AddWarpServer<TestContext>(x =>
            {
                x.RequireDatabaseAtStartup = requireDatabase;
                Warp.Provider.SqlServer.SqlServerServiceConfiguration.UseSqlServer(x);
            });
        }

        return builder.Build();
    }

    private static WarpStartupGate NewGate(bool requireDatabase, TimeProvider time) =>
        new(Microsoft.Extensions.Options.Options.Create(new WarpConfiguration { RequireDatabaseAtStartup = requireDatabase }), time, [Mock.Of<IWarpServerPresence>()])
        {
            RetryWaiting = new SemaphoreSlim(0),
        };

    private sealed class TestServer : IDisposable
    {
        private readonly CapturingLoggerProvider _logs = new();

        public TestServer(bool requireDatabase)
        {
            Queries
                .Setup(x => x.ClaimEnqueuedJobsAsync(It.IsAny<DbContext>(), It.IsAny<string[]>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<ClaimWatermark?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Claimed.TrySetResult())
                .ReturnsAsync([]);

            var appDatabase = $"startup-app-{Guid.NewGuid():N}";
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Services.AddLogging(x => x.AddProvider(_logs));
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.Services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase(appDatabase));
            builder.Services.AddSingleton(Queries.Object);
            builder.Services.AddSingleton(Mock.Of<IWarpLockProvider>());
            builder.Services.AddSingleton(Mock.Of<IDatabaseExceptionClassifier>());
            builder.Services.AddSingleton<IWarpServerContextConfigurator>(new FaultyServerContextConfigurator(Database));
            builder.Services.AddWarpServer<TestContext>(x =>
            {
                x.RequireDatabaseAtStartup = requireDatabase;
                x.WorkerCount = 1;
                x.HealthCheckInterval = null;
            });
            Host = builder.Build();
            Gate = Host.Services.GetRequiredService<WarpStartupGate>();
            Gate.RetryWaiting = RetryWaiting;
        }

        public IHost Host { get; }

        public FakeTimeProvider Time { get; } = new();

        public DatabaseSwitch Database { get; } = new();

        public Mock<IWarpSqlQueries<TestContext>> Queries { get; } = new();

        public TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WarpStartupGate Gate { get; }

        public SemaphoreSlim RetryWaiting { get; } = new(0);

        public IReadOnlyList<CapturedLog> Logs => _logs.Logs;

        public void Dispose()
        {
            Host.Dispose();
            RetryWaiting.Dispose();
        }
    }

    // The database the server context writes to: in memory, refusing every save while Down.
    private sealed class DatabaseSwitch : SaveChangesInterceptor
    {
        private int _saves;

        public bool Down { get; set; } = true;

        public int Saves => Volatile.Read(ref _saves);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);

            return Down ? throw new DatabaseDownException() : ValueTask.FromResult(result);
        }
    }

    private sealed class FaultyServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"startup-server-{Guid.NewGuid():N}";
        private readonly DatabaseSwitch _switch;

        public FaultyServerContextConfigurator(DatabaseSwitch databaseSwitch) => _switch = databaseSwitch;

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database).AddInterceptors(_switch);
        }
    }

    // What a provider throws when it cannot connect: a DbException with a connection-class SQLSTATE (08006 by default).
    private sealed class DatabaseDownException : DbException
    {
        public DatabaseDownException()
            : base("database unreachable")
        {
        }

        public DatabaseDownException(string message)
            : base(message)
        {
        }

        public DatabaseDownException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public DatabaseDownException(string message, int errorCode)
            : base(message, errorCode)
        {
        }

        public string? State { get; init; } = "08006";

        public override string? SqlState => State;
    }
}
