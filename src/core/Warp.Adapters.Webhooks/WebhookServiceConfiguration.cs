using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Adapters.Http;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Webhooks;

namespace Warp.Adapters.Webhooks;

/// <summary>
/// Opt-in registration for durable outbound webhook delivery. <c>AddWebhooks(w =&gt; ...)</c> wires the
/// dispatcher, the executor job handler, and — automatically — the <c>warp-webhooks</c> HTTP adapter that
/// every attempt flows through (so the attempt timeline is just <c>AdapterCallLog</c> rows keyed by the
/// delivery id, no second attempt table). Everything describing a <em>delivery</em> rides the
/// <see cref="WebhookSend"/>; <c>AddWebhooks</c> configures infrastructure only.
/// <para>
/// Targets the non-generic <see cref="IWarpBuilder"/> receiver (adapters/background-service precedent):
/// the dispatcher's and executor's <c>TContext</c> is recovered from the concrete builder (always an
/// <see cref="IWarpBuilder{TContext}"/>). Call once — the underlying <c>AddAdapter("warp-webhooks", …)</c>
/// throws on a duplicate name.
/// </para>
/// <para>
/// A worker somewhere in the deployment must drain the dedicated <c>warp:webhooks</c> queue or deliveries
/// never fire. When <c>AddWebhooks</c> runs inside an <c>AddWarpServer</c> lambda with the worker enabled,
/// it appends <c>warp:webhooks</c> to the default worker group's queues idempotently, so the common
/// <c>AddWarpServer().AddWebhooks()</c> shape delivers with no manual queue wiring. An <c>AddWarp</c>-only
/// (publisher / dashboard) process has no worker to wire, so the queue is left untouched — those processes
/// stage deliveries and rely on a server elsewhere to run them.
/// </para>
/// </summary>
public static class WebhookServiceConfiguration
{
    public static IWarpBuilder AddWebhooks(this IWarpBuilder builder, Action<WarpWebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(x => x.ServiceType == typeof(IWebhookDispatcher)))
        {
            throw new InvalidOperationException(
                "Webhooks are already registered. Call AddWebhooks() once — a second call would re-register "
                + "the warp-webhooks adapter (which throws) and double-wire the executor handler.");
        }

        var options = new WarpWebhookOptions();
        configure?.Invoke(options);

        var contextType = ResolveContextType(builder);

        WireWebhookQueue(builder);

        // The warp-webhooks adapter is the single HTTP leg for every attempt. Every attempt (successes
        // included) lands a call-log row; response bodies are always captured for diagnosis while request
        // bodies are never captured because the payload already lives on the delivery row; and the call-log
        // retention is aligned to the delivery retention so attempt rows and delivery rows age together.
        builder.AddAdapter(WebhookDefaults.AdapterName, a =>
        {
            a.Recording.RecordCalls = CallRecording.All;
            a.Recording.CaptureResponseBodies = CaptureMode.Always;
            a.Recording.CaptureRequestBodies = CaptureMode.None;
            a.Recording.CallLogRetention = builder.Configuration.WebhookDeliveryRetention;
            a.Recording.GroupLabel = "Endpoint";
        });

        var dispatcherType = typeof(WebhookDispatcher<>).MakeGenericType(contextType);
        builder.Services.TryAddScoped(typeof(IWebhookDispatcher), dispatcherType);

        // Redelivery seam: Core's WebhookCommandService flips a settled delivery back to Pending and drives
        // this to enqueue the fresh executor job (Core cannot construct ExecuteWebhookDelivery — the addon
        // owns the IHttpClientFactory dependency line). Registered here (only inside AddWebhooks), so its
        // presence also gates the dashboard "webhooks" addon flag (IWarpAdapters-style marker).
        builder.Services.TryAddScoped<IWebhookRedeliveryEnqueuer, WebhookRedeliveryEnqueuer>();

        // The executor is resolved by the worker via DI (IJobHandler<ExecuteWebhookDelivery>). Registered
        // as the closed generic so it binds to the caller's TContext for the delivery-row state writes.
        var handlerType = typeof(ExecuteWebhookDeliveryHandler<>).MakeGenericType(contextType);
        builder.Services.TryAddScoped(typeof(IJobHandler<ExecuteWebhookDelivery>), handlerType);

        if (options.ExhaustedHandlerType is not null)
        {
            builder.Services.AddScoped(typeof(IWebhookDeliveryExhaustedHandler), options.ExhaustedHandlerType);
        }

        // Built-in Standard Webhooks signer — stateless, always available for WebhookSigning.StandardWebhooks.
        builder.Services.TryAddSingleton<StandardWebhooksSigner>();

        if (options.CustomSignerType is not null)
        {
            builder.Services.AddScoped(typeof(IWebhookSigner), options.CustomSignerType);
        }

        // Fail fast: if the host declared custom signing but wired no IWebhookSigner (neither via
        // UseCustomSigner<T>() nor directly in DI before this call), a WebhookSigning.Custom send would only
        // fault at execute time. Surface it here at AddWebhooks time instead.
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

    private static Type ResolveContextType(IWarpBuilder builder)
    {
        var contextType = builder.GetType()
            .GetInterfaces()
            .Where(x => x.IsGenericType)
            .Where(x => x.GetGenericTypeDefinition() == typeof(IWarpBuilder<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();

        return contextType ?? throw new InvalidOperationException(
            "AddWebhooks() could not determine the DbContext type from the Warp builder. Call it inside the "
            + "AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda so the dispatcher and "
            + "executor can resolve your context.");
    }

    // Appends warp:webhooks to the server builder's default worker-group queue list so a server host that
    // calls AddWebhooks drains the queue automatically. Reflection (mirroring ResolveContextType) keeps the
    // webhooks package decoupled from Warp.Worker, where WarpServerConfiguration lives: a server builder
    // exposes bool RunWorker + string[] Queues; an AddWarp-only builder exposes neither, so the shape probe
    // itself distinguishes the two — a publisher/dashboard process is skipped silently.
    private static void WireWebhookQueue(IWarpBuilder builder)
    {
        var builderType = builder.GetType();

        if (builderType.GetProperty("RunWorker")?.GetValue(builder) is not true)
        {
            return;
        }

        if (builderType.GetProperty("Queues") is not { } queuesProperty
            || queuesProperty.GetValue(builder) is not string[] queues)
        {
            return;
        }

        if (queues.Contains(WebhookDefaults.Queue, StringComparer.Ordinal))
        {
            return;
        }

        string[] updated = [.. queues, WebhookDefaults.Queue];
        queuesProperty.SetValue(builder, updated);
    }
}

/// <summary>
/// Infrastructure configuration for <see cref="WebhookServiceConfiguration.AddWebhooks"/>. Deliberately
/// small — everything describing a delivery rides the <see cref="WebhookSend"/>, not app-level config.
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
    /// Registers the <see cref="IWebhookSigner"/> used for sends that specify
    /// <c>WebhookSigning.Custom</c>. Prefer this over registering <see cref="IWebhookSigner"/> directly —
    /// it declares custom-signing intent so a missing signer is caught at <c>AddWebhooks</c> time.
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
/// Addon-side implementation of the Core <see cref="IWebhookRedeliveryEnqueuer"/> seam. Enqueues the
/// executor job on the dedicated webhooks queue through the shared <see cref="IPublisher"/> and commits
/// via the outbox, so the caller's redeliver reset (tracked on the same scoped context) and the new job
/// land in one <c>SaveChanges</c>.
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

/// <summary>Shared identifiers and defaults for the webhooks feature.</summary>
internal static class WebhookDefaults
{
    /// <summary>The dedicated queue the executor jobs run on.</summary>
    internal const string Queue = WebhookConstants.Queue;

    /// <summary>The auto-registered HTTP adapter every attempt flows through.</summary>
    internal const string AdapterName = WebhookConstants.AdapterName;

    /// <summary>Library built-in retry schedule when a send does not specify one.</summary>
    internal static readonly IReadOnlyList<TimeSpan> RetrySchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];
}

/// <summary>
/// Persisted string-column length caps mirrored from the <c>WebhookDelivery</c> EF configuration. The
/// dispatcher (the single build choke point) clamps caller input to these before insert so an over-long
/// value never fails the row write.
/// </summary>
internal static class WebhookColumnCaps
{
    internal const int EventType = 200;
    internal const int EventId = 200;
    internal const int Url = 2048;
    internal const int GroupName = 200;
    internal const int Reference = 200;
    internal const int Secret = 512;
}
