using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Webhooks;

/// <summary>
/// <see cref="IWebhookDispatcher"/> over the caller's <typeparamref name="TContext"/>. Builds the
/// self-contained <see cref="WebhookDelivery"/> row from the <see cref="WebhookSend"/> and stages both
/// the row and the first executor job through the shared <see cref="IPublisher"/> (same scoped context,
/// so one <c>SaveChanges</c> commits both — the outbox pattern). This is the single build choke point
/// where caller input becomes a persisted row, so every capped string column is clamped here to its
/// schema length before insert (adapters lesson: clamp at one choke point so an over-long caller value
/// never fails the row write).
/// </summary>
internal sealed class WebhookDispatcher<TContext> : IWebhookDispatcher
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;

    public WebhookDispatcher(
        TContext context,
        IPublisher publisher,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration)
    {
        _context = context;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
    }

    public async Task<Guid> SendAsync(WebhookSend send, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);

        // Validate at the single build choke point (W-3): reject the inputs that would otherwise fault deep
        // in the executor (an empty attempt timeline) or silently change behaviour (a truncated URL points at
        // a different destination; a truncated/invalid signing secret produces a bad signature). Capped
        // display strings (EventType/EventId/Group/Reference) are still clamped — truncating those is lossy
        // but harmless — but the destination URL and the signing secret are validated, never clamped.
        if (string.IsNullOrWhiteSpace(send.Url))
        {
            throw new ArgumentException("WebhookSend.Url is required.", nameof(send));
        }

        if (!Uri.TryCreate(send.Url, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new ArgumentException("WebhookSend.Url must be an absolute http or https URL.", nameof(send));
        }

        if (send.Url.Length > WebhookColumnCaps.Url)
        {
            throw new ArgumentException(
                $"WebhookSend.Url exceeds the {WebhookColumnCaps.Url}-character limit; truncating it would change the destination.",
                nameof(send));
        }

        if (string.IsNullOrWhiteSpace(send.EventType))
        {
            throw new ArgumentException("WebhookSend.EventType is required.", nameof(send));
        }

        if (send.RetrySchedule is not null && send.RetrySchedule.Any(x => x < TimeSpan.Zero))
        {
            throw new ArgumentException("WebhookSend.RetrySchedule entries must be non-negative.", nameof(send));
        }

        if (send.Secret is { Length: > WebhookColumnCaps.Secret })
        {
            throw new ArgumentException(
                $"WebhookSend.Secret exceeds the {WebhookColumnCaps.Secret}-character limit.",
                nameof(send));
        }

        if (send.Signing == WebhookSigning.StandardWebhooks)
        {
            ValidateStandardWebhooksSecret(send.Secret);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var schedule = send.RetrySchedule ?? WebhookDefaults.RetrySchedule;

        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = Clamp(send.EventType, WebhookColumnCaps.EventType),
            EventId = Clamp(string.IsNullOrWhiteSpace(send.EventId) ? Guid.NewGuid().ToString() : send.EventId, WebhookColumnCaps.EventId),
            Url = send.Url,
            HeadersJson = send.Headers is { Count: > 0 } ? JsonSerializer.Serialize(send.Headers) : null,
            GroupName = ClampOptional(send.Group, WebhookColumnCaps.GroupName),
            Reference = ClampOptional(send.Reference, WebhookColumnCaps.Reference),
            PayloadJson = send.Payload,
            SigningMode = send.Signing,
            Secret = send.Secret,
            RetrySchedule = [.. schedule],
            SuccessCodesJson = send.SuccessCodes is { Count: > 0 } ? JsonSerializer.Serialize(send.SuccessCodes) : null,
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            Application = _configuration.ApplicationName,
            ExpireAt = now + _configuration.WebhookDeliveryRetention,
        };

        await _context.Set<WebhookDelivery>().AddAsync(delivery, cancellationToken);

        // First attempt runs immediately: enqueued (not scheduled) so signal-driven pickup applies.
        await _publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = delivery.Id }, WebhookDefaults.Queue);

        await _publisher.SaveChangesAsync(cancellationToken);

        return delivery.Id;
    }

    // StandardWebhooks signs with the base64-decoded secret (optionally whsec_-prefixed). A missing or
    // non-base64 secret would only fault at attempt time (a caught, recorded failed attempt that never
    // succeeds); reject it up front where the caller gets a clear error.
    private static void ValidateStandardWebhooksSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException(
                "WebhookSigning.StandardWebhooks requires a non-empty Secret (the whsec_ signing secret).",
                nameof(secret));
        }

        var encoded = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        var buffer = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, buffer, out _))
        {
            throw new ArgumentException(
                "WebhookSigning.StandardWebhooks Secret must be base64 (optionally whsec_-prefixed).",
                nameof(secret));
        }
    }

    private static string Clamp(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string? ClampOptional(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max];
    }
}
