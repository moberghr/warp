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

// The one merged AddAdapters options bag of a service collection, and the DB recording descriptors it added (so a
// later call that changes the sink can take back exactly those). Registration-time state only.
internal sealed class AdapterRecordingRegistration
{
    public WarpAdapterOptions Options { get; } = new();

    public List<ServiceDescriptor> DatabaseDescriptors { get; } = [];
}

public static class AdapterServiceConfiguration
{
    public static IWarpBuilder AddAdapters(this IWarpBuilder builder, Action<WarpAdapterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The recording channel/recorder is a SINGLE per-process singleton, so the sink is a process-level
        // choice. Repeated calls merge into ONE options bag (the first call registers it), so the sink a caller
        // set explicitly wins whatever the call order: AddAdapter (HTTP/Refit) calls AddAdapters() with no
        // configure, which leaves the bag as it is, before or after an explicit AddAdapters(o => o.Sink = Otel).
        // Other WarpAdapterOptions fields are per-adapter (set via AddAdapter) and ignored here.
        var registration = builder.Services
            .Where(x => x.ServiceType == typeof(AdapterRecordingRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AdapterRecordingRegistration>()
            .FirstOrDefault();
        if (registration is null)
        {
            registration = new AdapterRecordingRegistration();
            builder.Services.AddSingleton(registration);
        }

        configure?.Invoke(registration.Options);

        // Presence marker for the dashboard "adapters" flag (AddWarp now always registers IWarpAdapters for
        // unconditional telemetry, so it can no longer gate the flag — this can). Registered regardless of
        // sink so the nav flag reports "recording enabled" even under Otel-only.
        builder.Services.TryAddSingleton<IAdapterRecordingMarker, AdapterRecordingMarker>();

        // Process-level recording settings for WarpAdapters (optional-marker pattern §2.9). Under Otel/Both
        // the captured call detail is attached to the adapter Client span instead of a DB row (§8.24); under
        // Database-only the span carries identity/outcome only and the detail goes to AdapterCallLog. One
        // registration, read from the merged options when first resolved, so it reflects the final sink.
        builder.Services.TryAddSingleton(_ => new AdapterRecordingSettings(registration.Options.Sink is RecordingSink.Otel or RecordingSink.Both));

        // Singletons so per-adapter cardinality state persists across calls. TryAdd throughout so a second
        // AddAdapters() is a no-op.
        builder.Services.TryAddSingleton<AdapterRegistry>();
        builder.Services.TryAddSingleton<IWarpAdapters, WarpAdapters>();

        // AddAdapters is non-generic, but the concrete builder is always an IWarpBuilder<TContext>; recover
        // TContext from it (used by the flusher and the rate limiter closed generics).
        var contextType = ResolveContextType(builder);

        // Database / Both: the DB-backed recorder owns the bounded channel + the flusher drains it onto the
        // user's TContext (§0.5 scope). Under Otel-only NEITHER is registered — no DB rows and no
        // Counter-aggregate writes for this surface (the call detail rides the adapter Client span and
        // aggregates come from the OTel meters instead, §8.24). IAdapterCallRecorder falls back to Core's
        // NullAdapterCallRecorder in that case. Every call re-syncs the descriptors to the merged sink, so the
        // last call (and with it the build) sees the recorder and flusher of the final choice, registered once.
        SyncDatabaseRecording(builder.Services, registration, contextType);

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

    private static void SyncDatabaseRecording(IServiceCollection services, AdapterRecordingRegistration registration, Type contextType)
    {
        foreach (var descriptor in registration.DatabaseDescriptors)
        {
            services.Remove(descriptor);
        }

        registration.DatabaseDescriptors.Clear();
        if (registration.Options.Sink is not (RecordingSink.Database or RecordingSink.Both))
        {
            return;
        }

        var flusherType = typeof(AdapterCallFlusher<>).MakeGenericType(contextType);
        var descriptors = new[]
        {
            ServiceDescriptor.Singleton(x => new DbAdapterCallRecorder(
                x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity)),
            ServiceDescriptor.Singleton<IAdapterCallRecorder>(x => x.GetRequiredService<DbAdapterCallRecorder>()),
            ServiceDescriptor.Singleton(typeof(IHostedService), flusherType),
        };

        // TryAdd semantics, as before: a recorder already registered by someone else is kept, and the flusher is
        // deduplicated on its closed type. Only what this method added is tracked, so only that is ever removed.
        foreach (var descriptor in descriptors)
        {
            var present = descriptor.ServiceType == typeof(IHostedService)
                ? services.Any(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == flusherType)
                : services.Any(x => x.ServiceType == descriptor.ServiceType);
            if (!present)
            {
                services.Add(descriptor);
                registration.DatabaseDescriptors.Add(descriptor);
            }
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
