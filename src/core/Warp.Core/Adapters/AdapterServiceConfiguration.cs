using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
    public static IWarpBuilder AddAdapters(this IWarpBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Presence marker for the dashboard "adapters" flag (AddWarp now always registers IWarpAdapters for
        // unconditional telemetry, so it can no longer gate the flag — this can).
        builder.Services.TryAddSingleton<IAdapterRecordingMarker, AdapterRecordingMarker>();

        // Singletons so per-adapter cardinality state + the bounded recording channel persist across
        // calls. TryAdd throughout so a second AddAdapters() is a no-op. The DB-backed recorder owns
        // the channel; IAdapterCallRecorder resolves to the same instance.
        builder.Services.TryAddSingleton<AdapterRegistry>();
        builder.Services.TryAddSingleton(x => new DbAdapterCallRecorder(
            x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity));
        builder.Services.TryAddSingleton<IAdapterCallRecorder>(x => x.GetRequiredService<DbAdapterCallRecorder>());
        builder.Services.TryAddSingleton<IWarpAdapters, WarpAdapters>();

        // The flusher drains the recorder channel onto the user's TContext (§0.5 scope). AddAdapters is
        // non-generic, but the concrete builder is always an IWarpBuilder<TContext>; recover TContext
        // from it and register the closed generic flusher. TryAddEnumerable dedups on the closed type.
        var contextType = ResolveContextType(builder);
        var flusherType = typeof(AdapterCallFlusher<>).MakeGenericType(contextType);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));

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
