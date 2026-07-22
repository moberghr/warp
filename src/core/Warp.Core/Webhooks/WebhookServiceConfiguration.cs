using Microsoft.Extensions.DependencyInjection;

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
/// There is deliberately no "enable webhooks" gate: a server drains the <c>warp:webhooks</c> queue whether
/// or not <c>AddWebhooks</c> was called, so a delivery staged by any process is executed by any Warp server
/// in the deployment — no per-process opt-in to forget.
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
/// Enqueues the webhook executor job for a redelivery through the shared <see cref="IPublisher"/> and
/// commits it together with any pending changes on the shared scoped context (outbox), so the caller's
/// settled→Pending reset and the new job land in one <c>SaveChanges</c>. Registered unconditionally by
/// <c>AddWarp</c> (webhooks are part of Core), so <c>Redeliver</c> works in any process.
/// </summary>
internal sealed class WebhookRedeliveryEnqueuer : IWebhookRedeliveryEnqueuer
{
    private readonly IPublisher _publisher;

    public WebhookRedeliveryEnqueuer(IPublisher publisher) => _publisher = publisher;

    public async Task EnqueueAsync(Guid deliveryId, CancellationToken ct = default)
    {
        // Immediate (not scheduled) so signal-driven pickup applies — mirrors the first attempt in SendAsync.
        await _publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, WebhookDefaults.Queue);
        await _publisher.SaveChangesAsync(ct);
    }
}
