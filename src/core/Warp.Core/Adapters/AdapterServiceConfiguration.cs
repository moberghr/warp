using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Warp.Core.Observability;

namespace Warp.Core.Adapters;

/// <summary>
/// Opt-in registration for outbound adapter observability. Targets the non-generic
/// <see cref="IWarpBuilder"/> receiver (mirrors the <c>AddBackgroundService</c> precedent, §2.13) —
/// recording state is protocol-agnostic and does not need the user's <c>TContext</c> at the call site;
/// the flusher's <c>TContext</c> is recovered from the concrete builder (which is always an
/// <see cref="IWarpBuilder{TContext}"/>).
/// <para>
/// The two adapter entities are always in the schema regardless of this call (§2.11); this method
/// gates the runtime recording services only. OTel spans and <c>warp.adapter.*</c> meters flow
/// unconditionally from every completed <see cref="AdapterCallScope"/>.
/// </para>
/// </summary>
/// <summary>
/// Presence marker for opt-in adapter DB recording. Registered only by <see cref="AdapterServiceConfiguration.AddAdapters"/>,
/// so the dashboard "adapters" nav flag can distinguish "recording enabled" from the now-unconditional
/// <see cref="IWarpAdapters"/> (which is always registered by <c>AddWarp</c> for telemetry, §2.15). Mirrors
/// the <c>IDashboardPushMarker</c> precedent.
/// </summary>
public interface IAdapterRecordingMarker;

internal sealed class AdapterRecordingMarker : IAdapterRecordingMarker;

public static class AdapterServiceConfiguration
{
    public static IWarpBuilder AddAdapters(this IWarpBuilder builder, Action<WarpAdapterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The recording channel/recorder is a SINGLE per-process singleton, so the sink is a process-level
        // choice: build a throwaway options bag, apply the caller's config, and read Sink to select the
        // recorder wiring. Other WarpAdapterOptions fields are per-adapter (set via AddAdapter) and ignored here.
        var options = new WarpAdapterOptions();
        configure?.Invoke(options);
        var sink = options.Sink;

        // Presence marker for the dashboard "adapters" flag (AddWarp now always registers IWarpAdapters for
        // unconditional telemetry, so it can no longer gate the flag — this can). Registered regardless of
        // sink so the nav flag reports "recording enabled" even under Otel-only.
        builder.Services.TryAddSingleton<IAdapterRecordingMarker, AdapterRecordingMarker>();

        // Singletons so per-adapter cardinality state persists across calls. TryAdd throughout so a second
        // AddAdapters() is a no-op.
        builder.Services.TryAddSingleton<AdapterRegistry>();
        builder.Services.TryAddSingleton<IWarpAdapters, WarpAdapters>();

        // AddAdapters is non-generic, but the concrete builder is always an IWarpBuilder<TContext>; recover
        // TContext from it (used by the flusher and the rate limiter closed generics).
        var contextType = ResolveContextType(builder);

        // Database / Both: the DB-backed recorder owns the bounded channel + the flusher drains it onto the
        // user's TContext (§0.5 scope). TryAddEnumerable dedups the flusher on the closed type. Under
        // Otel-only NEITHER is registered — no DB rows and no Counter-aggregate writes for this surface
        // (aggregates come from OTel meters instead).
        if (sink is RecordingSink.Database or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton(x => new DbAdapterCallRecorder(
                x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity));

            var flusherType = typeof(AdapterCallFlusher<>).MakeGenericType(contextType);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));
        }

        // Otel / Both: Core ships the OTel recorder (structured OTLP log per call; no DB writes).
        if (sink is RecordingSink.Otel or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton<OtelAdapterCallRecorder>();
        }

        // Bind IAdapterCallRecorder to the selected recorder (or a composite fanning to both). TryAdd wins
        // over Core's fallback NullAdapterCallRecorder (AddAdapters runs earlier in the AddWarp lambda).
        RegisterRecorder(builder.Services, sink);

        // The shared rate limiter is keyed on TContext (row-locked leasing on RateLimitBucket). Registered
        // unconditionally but resolved lazily — only a rate-limited HTTP adapter's innermost handler pulls
        // it, and that path always has a provider (IWarpSqlQueries). Manual-scope-only / dashboard-only
        // processes never resolve it.
        var limiterType = typeof(AdapterRateLimiter<>).MakeGenericType(contextType);

        // TryAddSingleton silently keeps the first registration, so two AddWarp<TContext>() builders each
        // calling AddAdapters() would bind the limiter (and the whole recording pipeline) to the first
        // context and drop the second's — the calls appear to succeed but the second context is a phantom.
        // Same context twice stays idempotent; a DIFFERENT context is a misconfiguration, so throw.
        var existing = builder.Services.FirstOrDefault(x => x.ServiceType == typeof(IAdapterRateLimiter));
        if (existing?.ImplementationType is { IsGenericType: true } existingImpl
            && existingImpl.GetGenericTypeDefinition() == typeof(AdapterRateLimiter<>)
            && existingImpl != limiterType)
        {
            throw new InvalidOperationException(
                $"Adapters are already registered for DbContext '{existingImpl.GetGenericArguments()[0].Name}'; a "
                + $"second AddAdapters() bound to '{contextType.Name}' was rejected. Adapters support a single "
                + "TContext per process — call AddAdapters() from only one AddWarp<TContext>() builder.");
        }

        builder.Services.TryAddSingleton(typeof(IAdapterRateLimiter), limiterType);

        return builder;
    }

    private static void RegisterRecorder(IServiceCollection services, RecordingSink sink)
    {
        switch (sink)
        {
            case RecordingSink.Otel:
                services.TryAddSingleton<IAdapterCallRecorder>(x => x.GetRequiredService<OtelAdapterCallRecorder>());

                break;

            case RecordingSink.Both:
                services.TryAddSingleton<IAdapterCallRecorder>(x => new CompositeAdapterCallRecorder(
                    x.GetRequiredService<DbAdapterCallRecorder>(),
                    x.GetRequiredService<OtelAdapterCallRecorder>()));

                break;

            default:
                services.TryAddSingleton<IAdapterCallRecorder>(x => x.GetRequiredService<DbAdapterCallRecorder>());

                break;
        }
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
            "AddAdapters() could not determine the DbContext type from the Warp builder. Call it inside the "
            + "AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda so the adapter flusher can "
            + "resolve your context.");
    }
}
