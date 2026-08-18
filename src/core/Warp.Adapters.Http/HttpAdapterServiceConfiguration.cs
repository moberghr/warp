using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core;
using Warp.Core.Adapters;

namespace Warp.Adapters.Http;

/// <summary>
/// Registers named, observed <see cref="HttpClient"/> adapters. <c>AddAdapter("vendor", a =&gt; ...)</c>
/// wires an <see cref="IHttpClientFactory"/> client whose outermost handler is the
/// <c>WarpAdapterHandler</c> — every call through the client is named, timed, captured, and recorded with
/// no per-call code. Ensures the Core adapter services (<c>AddAdapters()</c>) are present so recording and
/// telemetry flow.
/// </summary>
public static class HttpAdapterServiceConfiguration
{
    /// <summary>
    /// Registers an HTTP adapter named <paramref name="name"/>. The name is the adapter's cluster-wide
    /// identity (stats merge and shared limits coordinate by name). Configure the base address, capture
    /// tiers, shared rate limit, and typed/custom clients via <paramref name="configure"/>.
    /// </summary>
    public static IWarpBuilder AddAdapter(this IWarpBuilder builder, string name, Action<WarpAdapterHttpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AdapterName.Validate(name);
        ArgumentNullException.ThrowIfNull(configure);

        // A second AddAdapter with the same name would double-wire the WarpAdapterHandler (named-options
        // handler actions accumulate on the IHttpClientFactory client), doubling every recorded call. The
        // per-adapter AdapterRegistrationEntry singletons already in the collection are the registration
        // ledger — reject a duplicate name up front.
        if (IsAlreadyRegistered(builder.Services, name))
        {
            throw new InvalidOperationException(
                $"Adapter '{name}' is already registered. Call AddAdapter once per adapter name — a second "
                + "registration would double-wire the recording handler and double every recorded call.");
        }

        // Recording + telemetry services live in Core and are idempotent (TryAdd). Calling here means a
        // consumer does not have to remember a separate AddAdapters() for the HTTP path to record.
        builder.AddAdapters();
        builder.Services.TryAddSingleton<OperationNameResolver>();

        var options = new WarpAdapterHttpOptions();
        configure(options);

        // Contribute this adapter's recording options + non-secret config summary to the DI-resolved
        // AdapterRegistry (§0.5 public-seam DTO). Without this the registry falls back to unknown-name
        // defaults and RecordCalls / EnrichCall / IncludeGroupInMetrics / GroupLabel / MaxDistinctGroups /
        // per-adapter CallLogRetention are all silently ignored, and ConfigSummary never reaches the
        // definition. Registered as a singleton so the registry folds it in before the first call.
        builder.Services.AddSingleton(new AdapterRegistrationEntry(name, options.Recording, BuildConfigSummary(options)));

        var httpBuilder = builder.Services.AddHttpClient(name, client =>
        {
            if (options.BaseUrl is not null)
            {
                client.BaseAddress = options.BaseUrl;
            }

            options.ClientConfigurator?.Invoke(client);
        });

        // Fixed ordering: the Warp handler is added first, so it is the outermost handler and observes one
        // logical call. User handlers (added by their configurators) nest inside it — that is where a retry
        // handler belongs; the shared rate-limit handler (added below) is innermost, so each physical
        // attempt spends its own token.
        httpBuilder.AddHttpMessageHandler(sp => new WarpAdapterHandler(
            name,
            options,
            sp.GetRequiredService<IWarpAdapters>(),
            sp.GetRequiredService<OperationNameResolver>()));

        foreach (var configurator in options.BuilderConfigurators)
        {
            configurator(httpBuilder);
        }

        // Innermost handler (added last): one shared token per physical attempt. Sits inside any user retry
        // handler so each retry attempt spends its own token — the vendor counts attempts, not logical calls.
        if (options.SharedRateLimit is { } rateLimit)
        {
            httpBuilder.AddHttpMessageHandler(sp => new WarpAdapterRateLimitHandler(
                name,
                rateLimit.Limit,
                rateLimit.PerSeconds,
                rateLimit.Overflow,
                rateLimit.MaxWait ?? TimeSpan.FromSeconds(rateLimit.PerSeconds),
                sp.GetRequiredService<IAdapterRateLimiter>()));
        }

        return builder;
    }

    private static bool IsAlreadyRegistered(IServiceCollection services, string name)
        => services.Any(x =>
            x.ServiceType == typeof(AdapterRegistrationEntry)
            && x.ImplementationInstance is AdapterRegistrationEntry entry
            && string.Equals(entry.Name, name, StringComparison.Ordinal));

    // Non-secret one-liner for the dashboard: capture tiers and the shared-limit shape. Deliberately
    // carries no URLs, headers, or payloads (§1.2) — only registration-time policy. Resilience is not
    // reported: it is a user-supplied handler now (ConfigureHttpClientBuilder), so registration cannot
    // see whether one is present.
    private static string BuildConfigSummary(WarpAdapterHttpOptions options)
    {
        var recording = options.Recording;
        var sharedLimit = options.SharedRateLimit is { } limit
            ? $"{limit.Limit}/{limit.PerSeconds}s ({limit.Overflow})"
            : "none";

        // Only surface the sample rate when it deviates from the keep-all default, so existing summaries stay stable.
        var sample = recording.SampleRate < 1.0
            ? $"; sample={recording.SampleRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"record={recording.RecordCalls}; capture req-body={recording.CaptureRequestBodies}, resp-body={recording.CaptureResponseBodies}, headers={recording.CaptureHeaders}; shared-limit={sharedLimit}{sample}";
    }
}
