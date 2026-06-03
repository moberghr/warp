using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Handlers;

namespace Warp.Core.Sagas;

public static class SagaServiceConfiguration
{
    /// <summary>
    /// Contributes the <see cref="Data.Entities.SagaState"/> entity to the user's DbContext and
    /// registers the saga infrastructure services. Call once per <c>AddWarp</c>/<c>AddWarpWorker</c>
    /// lambda. Individual saga handlers are registered separately via
    /// <see cref="AddSagaHandler{THandler}"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>opt.UsePostgreSql()</c> or <c>opt.UseSqlServer()</c> to have been called first —
    /// the saga proxy depends on <c>IWarpLockProvider</c>, which the provider package
    /// registers. Calling <c>AddSagas()</c> before a provider throws at configuration time.
    /// </remarks>
    public static IWarpBuilder<TContext> AddSagas<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext
    {
        // The proxy needs IWarpLockProvider (sp_getapplock / pg_try_advisory_lock backed) for
        // cross-process serialization. The provider is registered by Warp.Provider.PostgreSql
        // and Warp.Provider.SqlServer when the user calls the provider configuration helper.
        // Without it, the proxy fails on the first message with a DI resolution error — better
        // to fail loudly at startup. Earlier revisions used the semaphore provider with one
        // slot, which exhibited a hang under SQL Server contention because Medallion's row-
        // based semaphore protocol can BLOCK on transaction row locks even with timeout zero
        // (reproduced as a 20s TimedFact hang in TwoConcurrentMessagesSameSaga in the #211
        // investigation).
        if (!builder.Services.Any(d => d.ServiceType == typeof(IWarpLockProvider)))
        {
            throw new InvalidOperationException(
                "AddSagas() requires IWarpLockProvider to be registered. " +
                "Call opt.UsePostgreSql() or opt.UseSqlServer() BEFORE opt.AddSagas().");
        }

        // SagaState and SagaJobLink entities are registered unconditionally by
        // WarpModelCustomizer. This opt-in only wires the runtime saga services.
        //
        // TryAdd* across the board so a second AddSagas() call is a no-op rather than a
        // silent double-registration. SagaCorrelationCache in particular is a singleton —
        // plain AddSingleton would register two instances on the second call.
        builder.Services.TryAddScoped<ISagaStore, SagaStore<TContext>>();
        builder.Services.TryAddSingleton<SagaCorrelationCache>();

        // Dashboard query/command services. Registered as part of the addon so they're available
        // when the user calls AddSagas — the dashboard endpoints probe for them and return 404
        // when they're not registered.
        builder.Services.TryAddScoped<Services.ISagaQueryService, Services.SagaQueryService<TContext>>();
        builder.Services.TryAddScoped<Services.ISagaCommandService, Services.SagaCommandService<TContext>>();

        return builder;
    }

    /// <summary>
    /// Registers a saga handler class. Reflects over the implemented
    /// <c>ISagaHandler&lt;TSaga, TMessage&gt;</c> interfaces and registers each as an
    /// <see cref="IMessageHandler{TMessage}"/> via a generated
    /// <see cref="SagaHandlerProxy{TSaga, TMessage}"/>.
    /// </summary>
    public static IServiceCollection AddSagaHandler<THandler>(this IServiceCollection services)
        where THandler : class
    {
        var handlerType = typeof(THandler);

        var sagaInterfaces = handlerType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ISagaHandler<,>))
            .ToArray();

        if (sagaInterfaces.Length == 0)
        {
            throw new InvalidOperationException(
                $"{handlerType.FullName} does not implement ISagaHandler<,>. " +
                $"Saga handlers must implement at least one closed ISagaHandler<TSaga, TMessage> interface.");
        }

        // Idempotency guard: calling AddSagaHandler<H>() twice would otherwise register the proxy
        // twice for each (TSaga, TMessage), and MessageRouter would create two child jobs per
        // saga message — silent double-processing. Throw loudly instead.
        if (services.Any(d => d.ServiceType == handlerType && d.ImplementationType == handlerType))
        {
            throw new InvalidOperationException(
                $"AddSagaHandler<{handlerType.Name}>() was called more than once. " +
                $"Each saga handler must be registered exactly once.");
        }

        services.AddScoped(handlerType);

        foreach (var iface in sagaInterfaces)
        {
            var sagaType = iface.GenericTypeArguments[0];
            var messageType = iface.GenericTypeArguments[1];

            // Loud startup-time guard: a [Correlate] property named Email/Phone/SSN/etc. is
            // almost always a mistake. Throwing here is preferred to silently shipping PII
            // into JobLog/OTel tags. Users can opt out via [Correlate(IsAnonymized = true)].
            SagaPiiCheck.ValidateMessageType(messageType);

            // Resolving ISagaHandler<TSaga, TMessage> returns the same THandler instance the
            // proxy uses — the proxy's typed constructor parameter matches this registration.
            services.AddScoped(iface, sp => sp.GetRequiredService(handlerType));

            var proxyType = typeof(SagaHandlerProxy<,>).MakeGenericType(sagaType, messageType);
            var messageHandlerType = typeof(IMessageHandler<>).MakeGenericType(messageType);
            services.AddScoped(messageHandlerType, proxyType);
        }

        return services;
    }
}
