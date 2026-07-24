using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Handlers;

namespace Warp.Core.Sagas.Testing;

/// <summary>
/// Drives the full saga dispatch — correlate, load-or-create, mutex, converge, complete,
/// dead-letter, timeout-drop — against a configured <typeparamref name="TContext"/>
/// <b>without booting a worker or the message router</b>. The harness resolves the registered
/// <see cref="SagaHandlerProxy{TSaga, TMessage}"/> (an <see cref="IMessageHandler{TMessage}"/>),
/// invokes it directly with a usable <see cref="IJobContext"/>, lets the proxy persist through the
/// real <see cref="ISagaStore"/>, and classifies the effect into a <see cref="SagaDispatchResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wire it with a relational in-memory database (SQLite <c>DataSource=:memory:</c> on an open
/// connection is ideal — it honours the optimistic-concurrency <c>Version</c> token and the unique
/// <c>(Type, CorrelationKey)</c> index), the <see cref="InProcessLockProvider"/>, <c>AddSagas()</c>,
/// and one <c>AddSagaHandler&lt;THandler&gt;()</c> per handler. Use <see cref="Create"/> for the
/// shortest setup, or the <see cref="SagaTestHarness{TContext}(IServiceProvider)"/> constructor to
/// bring a service provider you built (and schema you created) yourself.
/// </para>
/// <para>
/// <b>Timeouts are dispatched synchronously.</b> An <see cref="ITimeoutMessage"/> passed to
/// <see cref="DispatchAsync{TMessage}"/> invokes its handler immediately — the harness does not wait
/// out <see cref="ITimeoutMessage.Delay"/>. It exercises the handler-firing and drop-after-complete
/// behaviour, not the scheduling latency (that belongs to <c>ScheduledJobActivation</c>).
/// </para>
/// </remarks>
/// <typeparam name="TContext">The user's Warp-configured <see cref="DbContext"/> type.</typeparam>
public sealed class SagaTestHarness<TContext> : IAsyncDisposable
    where TContext : DbContext
{
    private readonly IServiceProvider _services;
    private readonly bool _ownsProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SagaTestHarness{TContext}"/> class over a service
    /// provider the caller built (via <c>AddDbContext&lt;TContext&gt;</c> + <c>AddWarp&lt;TContext&gt;</c>
    /// + <see cref="InProcessLockProviderExtensions.UseInProcessLock"/> + <c>AddSagas()</c> +
    /// <c>AddSagaHandler&lt;T&gt;()</c>). The caller owns the provider's lifetime and is responsible for
    /// creating the schema (e.g. <c>Database.EnsureCreated()</c>).
    /// </summary>
    public SagaTestHarness(IServiceProvider services)
        : this(services, ownsProvider: false)
    {
    }

    private SagaTestHarness(IServiceProvider services, bool ownsProvider)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _ownsProvider = ownsProvider;
    }

    /// <summary>
    /// Builds a self-contained harness: registers <typeparamref name="TContext"/> with
    /// <paramref name="configureContext"/> (e.g. <c>o =&gt; o.UseSqlite(connection)</c>), wires
    /// <c>AddWarp</c> + <see cref="InProcessLockProviderExtensions.UseInProcessLock"/> +
    /// <c>AddSagas()</c>, runs <paramref name="configureServices"/> (where you register saga handlers),
    /// then creates the schema with <c>EnsureCreated()</c>. The returned harness owns the provider and
    /// disposes it on <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="configureContext">Configures the <typeparamref name="TContext"/> provider/connection.</param>
    /// <param name="configureServices">Registers saga handlers (and any optional addons) on the container.</param>
    /// <param name="timeProvider">Optional time source (e.g. a <c>FakeTimeProvider</c>) for controllable time.</param>
    /// <param name="schema">Warp schema; <c>null</c> (the default) suits SQLite, which has no schemas.</param>
    public static SagaTestHarness<TContext> Create(
        Action<DbContextOptionsBuilder> configureContext,
        Action<IServiceCollection> configureServices,
        TimeProvider? timeProvider = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configureContext);
        ArgumentNullException.ThrowIfNull(configureServices);

        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddDbContext<TContext>(configureContext);
        services.AddWarp<TContext>(opt =>
        {
            opt.Schema = schema;
            opt.UseInProcessLock();
            opt.AddSagas();
        });

        configureServices(services);

        // IDatabaseExceptionClassifier is normally contributed by a DB provider package; a harness
        // wired with only the in-process lock has none. Supply a no-op default (single-process
        // dispatch never races into the unique/version-conflict branches) unless the caller already
        // registered one.
        services.TryAddSingleton<IDatabaseExceptionClassifier, NoConflictExceptionClassifier>();

        var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreated();
        }

        return new SagaTestHarness<TContext>(provider, ownsProvider: true);
    }

    /// <summary>
    /// Dispatches <paramref name="message"/> through its registered saga proxy and returns the
    /// classified <see cref="SagaDispatchResult"/>. Opens a scope, sets a usable
    /// <see cref="IJobContext"/>, invokes every <see cref="IMessageHandler{TMessage}"/> registered for
    /// the message (the proxy persists via <see cref="ISagaStore"/> before returning), then classifies
    /// the effect from the saga row's before/after existence and the proxy-set outcome.
    /// </summary>
    public async Task<SagaDispatchResult> DispatchAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : class, IMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var handlers = sp.GetServices<IMessageHandler<TMessage>>().ToList();
        if (handlers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No saga handler is registered for message '{typeof(TMessage).FullName}'. " +
                $"Register one with services.AddSagaHandler<THandler>() where THandler implements " +
                $"ISagaHandler<TSaga, {typeof(TMessage).Name}>.");
        }

        var sagaTypeName = ResolveSagaTypeName(handlers, typeof(TMessage));
        var correlationKey = sp.GetRequiredService<SagaCorrelationCache>().GetCorrelationKey(message);

        var context = sp.GetRequiredService<TContext>();
        var existedBefore = await SagaRowExistsAsync(context, sagaTypeName, correlationKey, ct);

        var jobContext = sp.GetRequiredService<JobContext>();
        jobContext.JobId = Guid.NewGuid();
        jobContext.TraceId = Guid.NewGuid();

        foreach (var handler in handlers)
        {
            await handler.HandleAsync(message, ct);
        }

        // Re-query the same scoped context: the proxy committed through it, so a fresh (no-tracking)
        // read reflects the committed insert/delete without any stale identity-map entry.
        var existsAfter = await SagaRowExistsAsync(context, sagaTypeName, correlationKey, ct);

        var outcome = Classify<TMessage>(existedBefore, existsAfter, jobContext.Outcome);

        return new SagaDispatchResult
        {
            Outcome = outcome,
            JobOutcome = jobContext.Outcome,
        };
    }

    /// <summary>
    /// Loads the current saga of type <typeparamref name="TSaga"/> for <paramref name="correlationKey"/>
    /// through the real <see cref="ISagaStore"/>, or <c>null</c> if none is live. The key may be a
    /// <see cref="string"/>, <see cref="Guid"/>, <see cref="int"/>, or <see cref="long"/> — it is
    /// canonicalized the same way the dispatch pipeline canonicalizes a <c>[Correlate]</c> value.
    /// </summary>
    public async Task<TSaga?> GetSagaAsync<TSaga>(object correlationKey, CancellationToken ct = default)
        where TSaga : Saga, new()
    {
        ArgumentNullException.ThrowIfNull(correlationKey);

        var canonical = SagaCorrelationKeyConverter.ToCanonical(correlationKey);

        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaStore>();

        return await store.Load<TSaga>(canonical, ct);
    }

    /// <summary>Counts the live saga rows of type <typeparamref name="TSaga"/>.</summary>
    public async Task<int> CountAsync<TSaga>(CancellationToken ct = default)
        where TSaga : Saga
    {
        var typeName = typeof(TSaga).FullName!;

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        return await context.Set<SagaState>()
            .AsNoTracking()
            .Where(x => x.Type == typeName)
            .CountAsync(ct);
    }

    /// <summary>Disposes the underlying service provider when the harness owns it (built via <see cref="Create"/>).</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_ownsProvider)
        {
            return;
        }

        switch (_services)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
            default:
                break;
        }
    }

    private static SagaDispatchOutcome Classify<TMessage>(bool existedBefore, bool existsAfter, JobOutcome? outcome)
        where TMessage : class, IMessage
    {
        if (outcome is null)
        {
            if (!existedBefore && existsAfter)
            {
                return SagaDispatchOutcome.Created;
            }

            if (existedBefore && existsAfter)
            {
                return SagaDispatchOutcome.Updated;
            }

            // Row gone (completed) or a start message that completed in the same call and was never
            // persisted — both are logical completion with no requeue/failure outcome.
            return SagaDispatchOutcome.Completed;
        }

        var isTimeout = typeof(ITimeoutMessage).IsAssignableFrom(typeof(TMessage));
        if (isTimeout && !existedBefore)
        {
            return SagaDispatchOutcome.TimeoutDropped;
        }

        var hasStartsSaga = Attribute.IsDefined(typeof(TMessage), typeof(StartsSagaAttribute), inherit: false);
        if (!existedBefore && !hasStartsSaga)
        {
            return SagaDispatchOutcome.NotFound;
        }

        // A set outcome on an existing saga (or a start message) is a reschedule — the lock was held
        // (busy) or a save conflicted. Inspect JobOutcome.LogMessage to tell them apart.
        return SagaDispatchOutcome.Busy;
    }

    private static string ResolveSagaTypeName(IEnumerable<object> handlers, Type messageType)
    {
        foreach (var handler in handlers)
        {
            var type = handler.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SagaHandlerProxy<,>))
            {
                return type.GetGenericArguments()[0].FullName!;
            }
        }

        throw new InvalidOperationException(
            $"No saga handler proxy is registered for message '{messageType.FullName}'. " +
            $"The harness dispatches saga messages; register a handler with " +
            $"services.AddSagaHandler<THandler>().");
    }

    private static async Task<bool> SagaRowExistsAsync(TContext context, string sagaTypeName, string correlationKey, CancellationToken ct)
    {
        return await context.Set<SagaState>()
            .AsNoTracking()
            .Where(x => x.Type == sagaTypeName)
            .Where(x => x.CorrelationKey == correlationKey)
            .AnyAsync(ct);
    }

    // No-op classifier for the provider-less harness. Reports nothing as a conflict/deadlock, which
    // is correct for single-process synchronous dispatch — the concurrent-start and version-conflict
    // paths cannot occur without a second process contending on the same row.
    private sealed class NoConflictExceptionClassifier : IDatabaseExceptionClassifier
    {
        public bool IsUniqueConstraintViolation(DbUpdateException ex) => false;

        public bool IsTransientDeadlock(Exception ex) => false;
    }
}
