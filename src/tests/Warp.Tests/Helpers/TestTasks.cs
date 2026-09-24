using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Helpers;

/// <summary>
/// Factory helpers that construct fully-wired background task instances for unit tests.
/// Absorbs the ctor boilerplate (logger, options, lock provider, sql queries) so call sites
/// pass just the context + configurable knobs (timeouts etc.) that actually matter.
/// </summary>
public static class TestTasks
{
    /// <summary>
    /// Writes a worker's buffered counter increments out as <c>Counter</c> rows.
    /// <para>
    /// The worker sums increments into a <see cref="WarpCounterBuffer"/> and a background flusher
    /// writes them out on an interval, so a test that runs jobs and then asserts on Counter or
    /// Statistic rows must drain the buffer first rather than race that interval.
    /// </para>
    /// </summary>
    public static async Task FlushCountersAsync(WarpCounterBuffer buffer, DbContext context, CancellationToken ct = default)
    {
        await CreateCounterBufferFlusher(buffer, context).FlushOnceAsync(ct);
    }

    /// <summary>
    /// Builds the real <see cref="CounterBufferFlusher{TContext}"/> over a single context.
    /// <para>
    /// Tests drain through the production flusher rather than a stand-in that writes <c>Counter</c>
    /// rows itself. A stand-in silently diverges — the one written first truncated a long to an int
    /// while the flusher splits values past <c>int.MaxValue</c> across rows — and every test that used
    /// it would keep passing while the code it stood for was broken.
    /// </para>
    /// </summary>
    internal static CounterBufferFlusher<TestContext> CreateCounterBufferFlusher(
        WarpCounterBuffer buffer, DbContext context)
    {
        var services = new ServiceCollection();
        services.AddScoped<IWarpServerContext>(_ => new TestServerContext(context));

        return new CounterBufferFlusher<TestContext>(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            buffer,
            Options.Create(new WarpServerConfiguration()),
            NullLogger<CounterBufferFlusher<TestContext>>.Instance);
    }

    // Throwaway scope factory for tasks whose instance methods don't create scopes
    // (StaleJobRecoveryTask, ServerCleanupTask). MessageRoutingTask needs a real one with
    // registered handlers — pass it via the scopeFactory parameter.
    public static readonly IServiceScopeFactory EmptyScopeFactory =
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    public static readonly IWarpNotificationTransport NullTransport = new NullNotificationTransport();

    /// <summary>
    /// A no-op <see cref="ServerTaskSignals{TestContext}"/> for worker constructors in tests
    /// that don't exercise the orchestrator wake path. Cheap to share — no per-test state.
    /// </summary>
    public static readonly ServerTaskSignals<TestContext> NullSignals = new();

    public static IWarpSqlQueries<TContext> QueriesFor<TContext>(TContext context)
        where TContext : DbContext
    {
        var names = WarpJobTableNames.FromModel(context.Model);
        return IsPostgres(context)
            ? new PostgresWarpSqlQueries<TContext>(names)
            : new SqlServerWarpSqlQueries<TContext>(names);
    }

    public static IDatabaseExceptionClassifier ClassifierFor<TContext>(TContext context)
        where TContext : DbContext
    {
        return IsPostgres(context)
            ? new PostgresExceptionClassifier()
            : new SqlServerExceptionClassifier();
    }

    private static bool IsPostgres<TContext>(TContext context)
        where TContext : DbContext
    {
        return context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static IWarpSqlQueries<TContext> QueriesFromScope<TContext>(IServiceScopeFactory scopeFactory)
        where TContext : DbContext
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        return QueriesFor(context);
    }

    public static JobCommandService<TContext> CreateJobCommandService<TContext>(TContext context, TimeProvider? timeProvider = null)
        where TContext : DbContext
    {
        return new JobCommandService<TContext>(
            context,
            timeProvider ?? TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            NullTransport,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            new ServerTaskSignals<TContext>());
    }

    public static MessageRouter<TContext> CreateMessageRouter<TContext>(
        TContext context,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
        where TContext : DbContext
    {
        return new MessageRouter<TContext>(
            new TestServerContext(context),
            timeProvider,
            scopeFactory,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            NullTransport,
            new ServerTaskSignals<TContext>(),
            Options.Create(new WarpServerConfiguration()));
    }

    public static StaleJobRecovery<TContext> CreateStaleJobRecovery<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan invisibilityTimeout,
        bool restartByDefault = true,
        ServerTaskSignals<TContext>? signals = null,
        IWarpNotificationTransport? transport = null,
        string? applicationName = null)
        where TContext : DbContext
    {
        return new StaleJobRecovery<TContext>(
            new TestServerContext(context),
            timeProvider,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            Options.Create(new WarpServerConfiguration
            {
                InvisibilityTimeout = invisibilityTimeout,
                RestartStaleJobsByDefault = restartByDefault,
                ApplicationName = applicationName,
            }),
            transport ?? NullTransport,
            signals ?? new ServerTaskSignals<TContext>());
    }

    public static CounterAggregator<TContext> CreateCounterAggregator<TContext>(TContext context)
        where TContext : DbContext
    {
        return new CounterAggregator<TContext>(
            new TestServerContext(context),
            Options.Create(new WarpServerConfiguration()));
    }

    public static ScheduledJobActivation<TContext> CreateScheduledJobActivation<TContext>(
        TContext context,
        TimeProvider timeProvider,
        IWarpNotificationTransport? transport = null,
        ServerTaskSignals<TContext>? signals = null)
        where TContext : DbContext
    {
        return new ScheduledJobActivation<TContext>(
            new TestServerContext(context),
            timeProvider,
            transport ?? NullTransport,
            Options.Create(new WarpServerConfiguration()),
            QueriesFor(context),
            signals ?? new ServerTaskSignals<TContext>());
    }

    public static Orchestrator<TContext> CreateOrchestrator<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan jobExpirationTimeout,
        int? serverTaskBatchSize = null,
        IWarpNotificationTransport? transport = null,
        ServerTaskSignals<TContext>? signals = null)
        where TContext : DbContext
    {
        var configuration = new WarpServerConfiguration
        {
            JobExpirationTimeout = jobExpirationTimeout,
        };

        if (serverTaskBatchSize.HasValue)
        {
            configuration.ServerTaskBatchSize = serverTaskBatchSize.Value;
        }

        return new Orchestrator<TContext>(
            new TestServerContext(context),
            timeProvider,
            Options.Create(configuration),
            transport ?? NullTransport,
            signals ?? new ServerTaskSignals<TContext>());
    }

    public static RecurringJobScheduler<TContext> CreateRecurringJobScheduler<TContext>(
        TContext context,
        TimeProvider timeProvider,
        ServerTaskSignals<TContext>? signals = null,
        IWarpNotificationTransport? transport = null)
        where TContext : DbContext
    {
        return new RecurringJobScheduler<TContext>(
            new TestServerContext(context),
            timeProvider,
            transport ?? NullTransport,
            signals ?? new ServerTaskSignals<TContext>(),
            Options.Create(new WarpServerConfiguration()));
    }

    public static ExpirationCleanup<TContext> CreateExpirationCleanup<TContext>(
        TContext context,
        TimeProvider timeProvider,
        int batchSize = 1000)
        where TContext : DbContext
    {
        return new ExpirationCleanup<TContext>(
            new TestServerContext(context),
            timeProvider,
            Options.Create(new WarpServerConfiguration { ExpirationBatchSize = batchSize }),
            TestNotifiers.EmptyDispatcher());
    }

    public static ServerCleanup<TContext> CreateServerCleanup<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan healthCheckTimeout)
        where TContext : DbContext
    {
        return new ServerCleanup<TContext>(
            new TestServerContext(context),
            timeProvider,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            Options.Create(new WarpServerConfiguration { HealthCheckTimeout = healthCheckTimeout }),
            TestNotifiers.EmptyDispatcher());
    }
}
