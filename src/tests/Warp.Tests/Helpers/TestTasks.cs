using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data;
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

    public static JobCommandService<TContext> CreateJobCommandService<TContext>(TContext context)
        where TContext : DbContext
    {
        return new JobCommandService<TContext>(
            context,
            TimeProvider.System,
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
            context,
            timeProvider,
            scopeFactory,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            NullTransport,
            new ServerTaskSignals<TContext>(),
            Options.Create(new WarpWorkerConfiguration()));
    }

    public static StaleJobRecovery<TContext> CreateStaleJobRecovery<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan invisibilityTimeout,
        bool restartByDefault = true)
        where TContext : DbContext
    {
        return new StaleJobRecovery<TContext>(
            context,
            timeProvider,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            Options.Create(new WarpWorkerConfiguration
            {
                InvisibilityTimeout = invisibilityTimeout,
                RestartStaleJobsByDefault = restartByDefault,
            }));
    }

    public static CounterAggregator<TContext> CreateCounterAggregator<TContext>(TContext context)
        where TContext : DbContext
    {
        return new CounterAggregator<TContext>(
            context,
            Options.Create(new WarpWorkerConfiguration()));
    }

    public static ScheduledJobActivation<TContext> CreateScheduledJobActivation<TContext>(
        TContext context,
        TimeProvider timeProvider,
        IWarpNotificationTransport? transport = null)
        where TContext : DbContext
    {
        return new ScheduledJobActivation<TContext>(
            context,
            timeProvider,
            transport ?? NullTransport,
            Options.Create(new WarpWorkerConfiguration()),
            QueriesFor(context),
            new ServerTaskSignals<TContext>());
    }

    public static Orchestrator<TContext> CreateOrchestrator<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan jobExpirationTimeout,
        int? serverTaskBatchSize = null)
        where TContext : DbContext
    {
        var configuration = new WarpWorkerConfiguration
        {
            JobExpirationTimeout = jobExpirationTimeout,
        };

        if (serverTaskBatchSize.HasValue)
        {
            configuration.ServerTaskBatchSize = serverTaskBatchSize.Value;
        }

        return new Orchestrator<TContext>(
            context,
            timeProvider,
            Options.Create(configuration));
    }

    public static RecurringJobScheduler<TContext> CreateRecurringJobScheduler<TContext>(
        TContext context,
        TimeProvider timeProvider)
        where TContext : DbContext
    {
        return new RecurringJobScheduler<TContext>(
            context,
            timeProvider,
            Options.Create(new WarpWorkerConfiguration()));
    }

    public static ExpirationCleanup<TContext> CreateExpirationCleanup<TContext>(
        TContext context,
        TimeProvider timeProvider,
        int batchSize = 1000)
        where TContext : DbContext
    {
        return new ExpirationCleanup<TContext>(
            context,
            timeProvider,
            Options.Create(new WarpWorkerConfiguration { ExpirationBatchSize = batchSize }));
    }

    public static ServerCleanup<TContext> CreateServerCleanup<TContext>(
        TContext context,
        TimeProvider timeProvider,
        TimeSpan healthCheckTimeout)
        where TContext : DbContext
    {
        return new ServerCleanup<TContext>(
            context,
            timeProvider,
            Warp.Tests.Helpers.TestTasks.QueriesFor(context),
            Options.Create(new WarpWorkerConfiguration { HealthCheckTimeout = healthCheckTimeout }));
    }
}
