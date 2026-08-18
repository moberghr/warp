using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Adapters.Http;

/// <summary>
/// Per-adapter HTTP binding configuration passed to <c>AddAdapter("name", a =&gt; ...)</c>. Sugar over
/// the standard <see cref="IHttpClientBuilder"/> — never a wall: the escape hatches
/// (<see cref="ConfigureHttpClient"/>, <see cref="ConfigureHttpClientBuilder"/>) expose the raw builder
/// for mTLS, custom primary handlers, auth <c>DelegatingHandler</c>s, etc.
/// <para>
/// <b>Handler ordering is fixed</b> (not user-configurable): <c>WarpAdapterHandler</c> (outermost —
/// times the logical call and records one row) → your handlers (<see cref="ConfigureHttpClientBuilder"/>,
/// where a retry/resilience handler belongs) → the shared rate-limit handler
/// (<see cref="UseSharedRateLimit"/>, innermost — one token per physical attempt) → transport.
/// </para>
/// <para>
/// <see cref="BaseUrl"/> is optional: when unset, requests must carry absolute URIs and flow through the
/// identical pipeline (dynamic per-tenant hosts, webhook fan-out, per-service SOAP endpoints).
/// </para>
/// </summary>
public sealed class WarpAdapterHttpOptions
{
    private readonly List<Action<IHttpClientBuilder>> _builderConfigurators = [];

    /// <summary>
    /// Optional base address for the underlying <see cref="HttpClient"/>. Leave null for adapters whose
    /// requests carry absolute URIs (per-tenant hosts, webhook fan-out, per-service SOAP endpoints).
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// The protocol-agnostic observability configuration (record policy, capture tiers, truncation caps,
    /// redaction denylist, cardinality guards, group metrics). Shared verbatim with manual scopes and the
    /// Refit binding; the HTTP handler reads the capture tiers and redaction set from here.
    /// </summary>
    public WarpAdapterOptions Recording { get; } = new();

    internal Action<HttpClient>? ClientConfigurator { get; private set; }

    internal AdapterSharedRateLimit? SharedRateLimit { get; private set; }

    internal IReadOnlyList<Action<IHttpClientBuilder>> BuilderConfigurators => _builderConfigurators;

    /// <summary>Sets the base address and any other one-off <see cref="HttpClient"/> properties (timeout, default headers).</summary>
    public void ConfigureHttpClient(Action<HttpClient> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var existing = ClientConfigurator;
        ClientConfigurator = existing is null
            ? configure
            : client =>
            {
                existing(client);
                configure(client);
            };
    }

    /// <summary>
    /// Escape hatch onto the raw <see cref="IHttpClientBuilder"/> — add your own auth/logging/retry
    /// <c>DelegatingHandler</c>s, a custom primary handler for mTLS, etc. Your handlers nest inside the
    /// Warp handler and outside the shared rate-limit handler. May be called multiple times.
    /// <para>
    /// This is where resilience goes. Warp takes no retry dependency of its own; add the package you
    /// want and wire it here — e.g. <c>Microsoft.Extensions.Http.Resilience</c> via
    /// <c>ConfigureHttpClientBuilder(b =&gt; b.AddStandardResilienceHandler())</c>. Landing here keeps the
    /// documented ordering: retries stay inside the Warp handler (a call that succeeds on retry #3 is
    /// still one recorded row) and outside the shared rate limiter (one token per physical attempt).
    /// </para>
    /// </summary>
    public void ConfigureHttpClientBuilder(Action<IHttpClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _builderConfigurators.Add(configure);
    }

    /// <summary>
    /// Registers a typed client <typeparamref name="TClient"/> bound to this adapter's named client
    /// (passthrough to <see cref="HttpClientBuilderExtensions.AddTypedClient{TClient}(IHttpClientBuilder)"/>).
    /// </summary>
    public void AddTypedClient<TClient>()
        where TClient : class
        => _builderConfigurators.Add(builder => builder.AddTypedClient<TClient>());

    /// <summary>
    /// Enables the cluster-shared, DB-backed rate limiter (token leasing on the shared
    /// <c>RateLimitBucket</c>) for this adapter, keyed <c>warp:adapter:{name}</c>. One token per physical
    /// HTTP attempt. <paramref name="overflow"/> chooses <c>Wait</c> (bounded delay up to
    /// <paramref name="maxWait"/>) or <c>FailFast</c> (throw immediately). The runtime handler wiring is
    /// added by the shared rate-limiter batch.
    /// </summary>
    public void UseSharedRateLimit(int limit, int perSeconds, AdapterRateLimitOverflow overflow, TimeSpan? maxWait = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perSeconds, 1);

        SharedRateLimit = new AdapterSharedRateLimit(limit, perSeconds, overflow, maxWait);
    }
}

/// <summary>
/// Captured shared-rate-limit policy for an adapter. Consumed by the shared rate-limiter batch's
/// handler wiring; persisted onto <c>AdapterDefinition</c> for cluster coordination.
/// </summary>
internal sealed record AdapterSharedRateLimit(int Limit, int PerSeconds, AdapterRateLimitOverflow Overflow, TimeSpan? MaxWait);
