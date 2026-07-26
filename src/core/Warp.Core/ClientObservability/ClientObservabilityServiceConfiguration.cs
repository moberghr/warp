using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Warp.Core.Observability;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Opt-in registration for client (browser) observability (§8.27), called inside the <c>AddWarp</c> lambda:
/// <c>opt.AddClientObservability(o =&gt; o.AddIngestKey("app", "pk_..."))</c>. Registers the recording pipeline +
/// options + cardinality guard; the HTTP binding's <c>MapWarpClientObservability()</c> exposes the ingest
/// endpoint. The read service <see cref="Services.IClientEventQueryService"/> is registered by <c>AddWarp</c>
/// itself, so the dashboard serves <c>/api/client</c> even in processes that don't record. This method gates
/// the recorder/flusher (by sink, §8.24) and the addons flag (keyed on <see cref="IClientObservabilityMarker"/>).
/// </summary>
public static class ClientObservabilityServiceConfiguration
{
    public static IWarpBuilder AddClientObservability(
        this IWarpBuilder builder,
        Action<WarpClientObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WarpClientObservabilityOptions();
        configure?.Invoke(options);
        var sink = options.Sink;

        var contextType = ResolveContextType(builder);

        // Presence marker for the dashboard "client" flag, registered regardless of sink.
        builder.Services.TryAddSingleton<IClientObservabilityMarker, ClientObservabilityMarker>();

        // The cardinality guard is a process-level singleton (holds the distinct-name sets); needed under any
        // sink that writes counters. Registered with the DB pipeline only (Otel writes no counters).
        if (sink is RecordingSink.Database or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton(x =>
            {
                var o = x.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value;

                return new ClientEventCardinality(o.MaxDistinctErrorNames, o.MaxDistinctEventNames);
            });

            builder.Services.TryAddSingleton(x => new DbClientEventRecorder(
                x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity));
            builder.Services.TryAddSingleton<IClientEventRecorder>(x => x.GetRequiredService<DbClientEventRecorder>());

            var flusherType = typeof(ClientEventFlusher<>).MakeGenericType(contextType);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));
        }

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder;
    }

    private static Type ResolveContextType(IWarpBuilder builder)
    {
        var contextType = builder.GetType()
            .GetInterfaces()
            .Where(x => x.IsGenericType)
            .Where(x => x.GetGenericTypeDefinition() == typeof(IWarpBuilder<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();

        return contextType ?? throw new InvalidOperationException(
            "AddClientObservability() could not determine the DbContext type from the Warp builder. Call it "
            + "inside the AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda so the client "
            + "event flusher can resolve your context.");
    }
}
