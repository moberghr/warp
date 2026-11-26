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
    private readonly WebhookSendBehavior _behavior;
    private readonly bool _publicAddressesOnly;

    public WebhookDispatcher(
        TContext context,
        IPublisher publisher,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        IOptions<WebhookSendBehavior> behavior,
        IOptions<WebhookAddressPolicy> addressPolicy)
    {
        _publicAddressesOnly = addressPolicy.Value.PublicAddressesOnly;
        _context = context;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _behavior = behavior.Value;
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

        // With the public-address guard on in this process, a destination that can only be non-public is refused
        // here, where the caller gets a clear error, instead of failing every attempt. A host NAME is resolved at
        // attempt time by the guard (a DNS answer can change); only literals and localhost are known now.
        if (_publicAddressesOnly && IsNonPublicLiteral(uri))
        {
            throw new ArgumentException(
                $"WebhookSend.Url host '{uri.Host}' is not a public address, and PublicAddressesOnly is on. "
                + "Use a public destination, or set AddWebhooks(w => w.PublicAddressesOnly = false) for local development.",
                nameof(send));
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
            WebhookSecretValidation.ValidateStandardWebhooksSecret(send.Secret, nameof(send));
        }

        var eventId = Clamp(string.IsNullOrWhiteSpace(send.EventId) ? Guid.NewGuid().ToString() : send.EventId, WebhookColumnCaps.EventId);
        var groupName = ClampOptional(send.Group, WebhookColumnCaps.GroupName);

        // Dedupe (8.1, opt-in): the same event to the same URL returns the delivery already stored, in any status,
        // until retention deletes it. The unique index on DedupeKey backs the check against a concurrent send.
        string? dedupeKey = null;
        if (_behavior.DeduplicateByEventId)
        {
            // The caller's own event id, not the stored (clamped) one: two ids that differ only past the column
            // cap are different events.
            dedupeKey = WebhookDedupe.Key(string.IsNullOrWhiteSpace(send.EventId) ? eventId : send.EventId, send.Url);

            var existing = await _context.Set<WebhookDelivery>()
                .AsNoTracking()
                .Where(x => x.DedupeKey == dedupeKey)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is { } existingId)
            {
                return existingId;
            }
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var schedule = send.RetrySchedule ?? WebhookDefaults.RetrySchedule;

        var delivery = new WebhookDelivery
        {
            Id = Warp.Core.Data.WarpId.Next(),
            EventType = Clamp(send.EventType, WebhookColumnCaps.EventType),
            EventId = eventId,
            Url = send.Url,
            HeadersJson = send.Headers is { Count: > 0 } ? JsonSerializer.Serialize(send.Headers) : null,
            GroupName = groupName,
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
            TrackEndpointHealth = _behavior.TrackEndpointHealth && groupName is not null,
            DedupeKey = dedupeKey,
        };

        await _context.Set<WebhookDelivery>().AddAsync(delivery, cancellationToken);

        // First attempt runs immediately: enqueued (not scheduled) so signal-driven pickup applies.
        await _publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = delivery.Id }, WebhookDefaults.Queue);

        await _publisher.SaveChangesAsync(cancellationToken);

        return delivery.Id;
    }

    private static bool IsNonPublicLiteral(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return System.Net.IPAddress.TryParse(uri.IdnHost, out var address) && !WebhookAddressGuard.IsPublic(address);
        }

        // RFC 6761: localhost and every name under it resolve to loopback (a trailing root dot is the same name).
        var host = uri.IdnHost.TrimEnd('.');

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
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
