using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;

namespace Warp.Core.Webhooks;

/// <summary>
/// Optional configuration for durable outbound webhook delivery. The delivery <b>engine</b> — dispatcher,
/// executor job handler, the auto-recorded <c>warp-webhooks</c> adapter, the redelivery enqueuer, and the
/// built-in signer — is part of Core and wired unconditionally by <c>AddWarp</c>/<c>AddWarpServer</c>
/// (webhooks live in <c>Warp.Core.Webhooks</c>; nothing happens until a caller invokes
/// <see cref="IWebhookDispatcher.SendAsync"/>, so always-on carries no cost). This method only registers the
/// <em>optional</em> host hooks — a custom <see cref="IWebhookSigner"/> and the
/// <see cref="IWebhookDeliveryExhaustedHandler"/> — and validates custom-signing intent at startup.
/// <para>
/// There is deliberately no "enable webhooks" gate: an executor job is an ordinary job on an ordinary queue
/// (<see cref="WarpWebhookOptions.Queue"/>, by default <c>DefaultQueue</c>), so any Warp server whose workers
/// drain that queue runs it, whether or not <c>AddWebhooks</c> was called there.
/// </para>
/// </summary>
public static class WebhookServiceConfiguration
{
    public static IWarpBuilder AddWebhooks(this IWarpBuilder builder, Action<WarpWebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WarpWebhookOptions();
        configure?.Invoke(options);

        if (options.ExhaustedHandlerType is not null)
        {
            builder.Services.AddScoped(typeof(IWebhookDeliveryExhaustedHandler), options.ExhaustedHandlerType);
        }

        if (options.CustomSignerType is not null)
        {
            builder.Services.AddScoped(typeof(IWebhookSigner), options.CustomSignerType);
        }

        // Fail fast: custom signing declared but no IWebhookSigner wired (neither via UseCustomSigner<T>()
        // nor directly in DI before this call) would only fault at execute time — surface it here.
        if (options.CustomSigningDeclared
            && options.CustomSignerType is null
            && !builder.Services.Any(x => x.ServiceType == typeof(IWebhookSigner)))
        {
            throw new InvalidOperationException(
                "AddWebhooks(w => w.UseCustomSigner()) declared custom signing but no IWebhookSigner is "
                + "registered. Register one with w.UseCustomSigner<T>(), or add IWebhookSigner to the service "
                + "collection before AddWebhooks. WebhookSigning.Custom must not fail at send time.");
        }

        if (options.EndpointHealthHandlerType is not null)
        {
            builder.Services.AddScoped(typeof(IWebhookEndpointHealthHandler), options.EndpointHealthHandlerType);
        }

        if (options.Queue is not null && string.IsNullOrWhiteSpace(options.Queue))
        {
            throw new InvalidOperationException("AddWebhooks(w => w.Queue = ...) must not be empty; leave it null to use DefaultQueue.");
        }

        // Only an explicit queue is applied, so a later AddWebhooks call that does not set one keeps the earlier one.
        if (options.Queue is { } queue)
        {
            builder.Services.Configure<WebhookSendBehavior>(x => x.Queue = queue);
        }

        // OR-merged across repeated AddWebhooks calls: a later call without the flag never turns it back off.
        builder.Services.Configure<WebhookSendBehavior>(x =>
        {
            x.TrackEndpointHealth |= options.TrackEndpointHealth;
            x.DeduplicateByEventId |= options.DeduplicateByEventId;
        });

        // Only an explicit setting configures the policy, so a later AddWebhooks call that does not mention it never
        // undoes an opt-out (or an opt-in) made earlier.
        if (options.PublicAddressesOnlySet is { } publicAddressesOnly)
        {
            builder.Services.Configure<WebhookAddressPolicy>(x => x.PublicAddressesOnly = publicAddressesOnly);
        }

        return builder;
    }
}

/// <summary>
/// Optional host hooks for webhook delivery. Deliberately small — everything describing a delivery rides
/// the <see cref="WebhookSend"/>, not app-level config, and the engine itself needs no configuration.
/// </summary>
public sealed class WarpWebhookOptions
{
    internal Type? ExhaustedHandlerType { get; private set; }

    internal Type? CustomSignerType { get; private set; }

    internal bool CustomSigningDeclared { get; private set; }

    internal Type? EndpointHealthHandlerType { get; private set; }

    /// <summary>
    /// The queue this process's webhook deliveries run on (8.2). <c>null</c> (the default) puts them on
    /// <c>DefaultQueue</c> like any other job. A <see cref="WebhookSend.Queue"/> overrides it per send. The queue is
    /// stamped on each delivery at <c>SendAsync</c>, so its retries and redeliveries stay on it. To keep slow
    /// receivers away from your job workers, give webhooks their own queue and drain it with its own
    /// <c>AddWorkerGroup</c>. A server also uses it for deliveries sent before 8.2, which carry no queue.
    /// </summary>
    public string? Queue { get; set; }

    /// <summary>
    /// When <see langword="true"/>, deliveries this process sends with a <c>Group</c> track their endpoint's health
    /// (8.1): a delivery that runs out of retries is <c>Parked</c> instead of <c>Exhausted</c> (no exhausted callback)
    /// and marks its group <c>Failing</c>; the endpoint monitor probes the group and,
    /// once a delivery succeeds, releases the parked ones oldest first. The choice is stamped on each delivery at
    /// <c>SendAsync</c>, so it holds whichever server runs the attempt. Deliveries without a group are not tracked.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool TrackEndpointHealth { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <c>SendAsync</c> in this process deduplicates on the event id and URL (8.1): if a
    /// delivery of the same <c>EventId</c> to the same <c>Url</c> still exists (any status, until retention deletes
    /// it), its id is returned and nothing new is stored or sent. Only meaningful when the caller sets
    /// <c>EventId</c>; a send without one gets a fresh id and never matches. Two concurrent sends of the same pair
    /// race on a unique index: one of them fails its save with a unique-constraint error. Default <see langword="false"/>.
    /// </summary>
    public bool DeduplicateByEventId { get; set; }

    /// <summary>
    /// Registers the host callback for endpoint health changes (started failing, recovered, still failing after
    /// <c>WebhookEndpointStillFailingAfter</c>). Equivalent to registering <typeparamref name="THandler"/> as
    /// <see cref="IWebhookEndpointHealthHandler"/> in DI. Warp never disables anything itself.
    /// </summary>
    public void OnEndpointHealthChanged<THandler>()
        where THandler : class, IWebhookEndpointHealthHandler
        => EndpointHealthHandlerType = typeof(THandler);

    /// <summary>
    /// Whether webhook attempts connect only to publicly routable addresses. <b>On by default since 8.1</b>: a
    /// delivery URL usually comes from a customer, and without the guard it could point at <c>localhost</c>, a private
    /// network or a cloud metadata endpoint. The <c>warp-webhooks</c> client resolves the destination host itself,
    /// drops every loopback, private, link-local (including 169.254.169.254), shared, documentation, multicast and
    /// reserved address (the IANA special-purpose registries, RFC 6890), and connects to exactly the addresses that
    /// passed — redirects included. A host that resolves to no public address fails the attempt with a
    /// <see cref="WebhookAddressNotPublicException"/> (retried per the schedule). In the sending process,
    /// <c>SendAsync</c> also rejects a URL whose host is a non-public IP literal or <c>localhost</c> with an
    /// <see cref="ArgumentException"/>. Set it to <see langword="false"/> for local development and for tests that
    /// stub the client: with the guard on, a stub primary handler fails every attempt closed (see below).
    /// <para>
    /// It applies per process: the attempt is guarded by the process that runs it, so leave it on in every server that
    /// runs a worker. <b>With an HTTP proxy</b> (an explicit <c>Proxy</c>, or <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>)
    /// the client connects to the proxy, so the guard checks the proxy's address and the proxy must enforce the policy.
    /// </para>
    /// <para>
    /// The guard needs a <see cref="SocketsHttpHandler"/> primary handler. Configure that client with
    /// <c>UseSocketsHttpHandler(...)</c>: its settings are kept, except a host <c>ConnectCallback</c>, which the
    /// guard replaces. A primary handler of another type (an <c>HttpClientHandler</c> carrying a proxy or client
    /// certificates, a test stub) is kept, never replaced, and every attempt through it fails closed with an error
    /// naming the fix (<c>UseSocketsHttpHandler</c>, or <c>PublicAddressesOnly = false</c>); a Warning is logged once.
    /// </para>
    /// </summary>
    public bool PublicAddressesOnly
    {
        get => PublicAddressesOnlySet ?? true;
        set => PublicAddressesOnlySet = value;
    }

    internal bool? PublicAddressesOnlySet { get; private set; }

    /// <summary>
    /// Registers the host callback invoked once when a delivery exhausts its retry schedule. Equivalent
    /// to registering <typeparamref name="THandler"/> as <see cref="IWebhookDeliveryExhaustedHandler"/> in DI.
    /// </summary>
    public void OnDeliveryExhausted<THandler>()
        where THandler : class, IWebhookDeliveryExhaustedHandler
        => ExhaustedHandlerType = typeof(THandler);

    /// <summary>
    /// Registers the <see cref="IWebhookSigner"/> used for sends that specify <c>WebhookSigning.Custom</c>.
    /// Prefer this over registering <see cref="IWebhookSigner"/> directly — it declares custom-signing
    /// intent so a missing signer is caught at <c>AddWebhooks</c> time.
    /// </summary>
    public void UseCustomSigner<TSigner>()
        where TSigner : class, IWebhookSigner
    {
        CustomSignerType = typeof(TSigner);
        CustomSigningDeclared = true;
    }

    /// <summary>
    /// Declares that this host uses <c>WebhookSigning.Custom</c> with an <see cref="IWebhookSigner"/> it
    /// registers itself in the service collection (before <c>AddWebhooks</c>). <c>AddWebhooks</c> validates
    /// the registration is present and throws at registration time if it is missing.
    /// </summary>
    public void UseCustomSigner() => CustomSigningDeclared = true;
}

/// <summary>
/// Enqueues the webhook executor job for a redelivery, on the delivery's own queue, through the shared <see cref="IPublisher"/> and
/// commits it together with any pending changes on the shared scoped context (outbox), so the caller's
/// settled→Pending reset and the new job land in one <c>SaveChanges</c>. Registered unconditionally by
/// <c>AddWarp</c> (webhooks are part of Core), so <c>Redeliver</c> works in any process.
/// </summary>
internal sealed class WebhookRedeliveryEnqueuer<TContext> : IWebhookRedeliveryEnqueuer
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IPublisher _publisher;
    private readonly WarpConfiguration _configuration;
    private readonly WebhookSendBehavior _behavior;

    public WebhookRedeliveryEnqueuer(TContext context, IPublisher publisher, IOptions<WarpConfiguration> configuration, IOptions<WebhookSendBehavior> behavior)
    {
        _context = context;
        _publisher = publisher;
        _configuration = configuration.Value;
        _behavior = behavior.Value;
    }

    public async Task EnqueueAsync(Guid deliveryId, CancellationToken ct = default)
    {
        // The delivery stays on the queue it was sent on; a row from before 8.2 has none and takes this process's.
        var deliveryQueue = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .Select(x => x.Queue)
            .FirstOrDefaultAsync(ct);

        // Immediate (not scheduled) so signal-driven pickup applies — mirrors the first attempt in SendAsync.
        await _publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, _behavior.ResolveQueue(deliveryQueue, _configuration));
        await _publisher.SaveChangesAsync(ct);
    }
}
