using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Warp.Demo.PartnerApi;
using Warp.Demo.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton<ReceiptStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

// The shared Standard Webhooks secret the subscriber verifies with (whsec_<base64>). Matches the value the
// shop app signs with; override via Webhooks:Secret.
var webhookSecret = app.Configuration["Webhooks:Secret"] ?? "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

#pragma warning disable CA5394 // Random is fine here — demo jitter, not security.
var random = Random.Shared;
#pragma warning restore CA5394

// ── Payment gateways (the shop charges cards here through the per-vendor payment adapters) ──────────
// The base decline/error rate varies by VENDOR (its own adapter): adyen flakiest, paypal middling,
// stripe cleanest — so each adapter's health reads differently. The CHANNEL (the adapter group) adds a
// decline modifier on top: marketplace declines much more (fraud checks), mobile a little, web none — so
// the per-Channel table shows a channel red across every vendor.
app.MapPost("/gateway/charge", (ChargeRequest charge) =>
{
    var (declineRate, errorRate) = charge.Provider switch
    {
        "adyen" => (0.20, 0.06),
        "paypal" => (0.12, 0.03),
        _ => (0.04, 0.015), // stripe
    };

    declineRate += charge.Channel switch
    {
        "marketplace" => 0.12,
        "mobile" => 0.03,
        _ => 0.0, // web
    };

    var roll = random.NextDouble();
    if (roll < errorRate)
    {
        return Results.Problem("Payment processor unavailable.", statusCode: StatusCodes.Status502BadGateway);
    }

    if (roll < errorRate + declineRate)
    {
        return Results.Json(
            new { error = "card_declined", charge.OrderId, charge.Provider, charge.Channel },
            statusCode: StatusCodes.Status402PaymentRequired);
    }

    return Results.Ok(new ChargeResult($"ch_{Guid.NewGuid():N}", charge.OrderId, charge.Amount, "captured"));
});

app.MapPost("/gateway/refund", (RefundRequest refund) =>
    Results.Ok(new { refundId = $"rf_{Guid.NewGuid():N}", refund.PaymentId, status = "refunded" }));

// ── Shipping carriers (the shop books labels + quotes rates through the per-vendor carrier adapters) ─
// Latency varies by carrier (its own adapter) so each carrier adapter's average duration reads
// differently; the channel (the group) adds a small latency modifier so the per-Channel table varies too.
app.MapGet("/carrier/rates/{sku}", async (string sku, string? carrier, string? channel, CancellationToken ct) =>
{
    var delayMs = carrier switch
    {
        "dhl" => random.Next(600, 1200),
        "fedex" => random.Next(300, 700),
        _ => random.Next(150, 400), // ups
    };
    delayMs += channel switch
    {
        "marketplace" => random.Next(150, 300),
        "mobile" => random.Next(50, 120),
        _ => 0, // web
    };
    await Task.Delay(delayMs, ct);

    return Results.Ok(new RateQuote(sku, carrier ?? "ups", Math.Round(((decimal)random.NextDouble() * 20) + 5, 2)));
});

app.MapPost("/carrier/shipments", (ShipmentRequest shipment) =>
    Results.Ok(new ShipmentResult(
        $"{(shipment.Carrier ?? "ups").ToUpperInvariant()}-{random.Next(100000, 999999)}",
        shipment.OrderId,
        shipment.Carrier ?? "ups",
        "label_created")));

// ── Subscriber (an external system the shop notifies via durable webhooks) ─────────────────────────
// Reliable — verifies the signature and always accepts. Deliveries here go straight to Delivered.
app.MapPost("/subscriber/webhooks", async (HttpRequest request, ReceiptStore store, CancellationToken ct) =>
    await ReceiveAsync(request, store, webhookSecret, failureRate: 0.0, ct));

// Unstable — accepts ~50% of the time, 503 otherwise: deliveries retry, some settle, some exhaust.
app.MapPost("/subscriber/webhooks/flaky", async (HttpRequest request, ReceiptStore store, CancellationToken ct) =>
    await ReceiveAsync(request, store, webhookSecret, failureRate: 0.5, ct));

// Down — always 503: every delivery runs its schedule out and goes Exhausted (host callback fires once).
app.MapPost("/subscriber/webhooks/down", async (HttpRequest request, ReceiptStore store, CancellationToken ct) =>
    await ReceiveAsync(request, store, webhookSecret, failureRate: 1.0, ct));

// ── Human-facing views ────────────────────────────────────────────────────────────────────────────
app.MapGet("/subscriber/events", (ReceiptStore store) => Results.Ok(store.Snapshot()));

app.MapGet("/subscriber", (ReceiptStore store) => Results.Content(store.RenderHtml(), "text/html"));

app.MapGet("/", () => Results.Redirect("/subscriber"));

await app.RunAsync();

async Task<IResult> ReceiveAsync(HttpRequest request, ReceiptStore store, string secret, double failureRate, CancellationToken ct)
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var payload = await reader.ReadToEndAsync(ct);

    var eventType = request.Headers.TryGetValue("webhook-event-type", out var et) ? et.ToString() : "(unknown)";
    var webhookId = request.Headers.TryGetValue("webhook-id", out var id) ? id.ToString() : null;
    var signatureValid = VerifySignature(request, payload, secret);

    // Record the attempt BEFORE simulating a flaky receiver, so the log shows rejected attempts too —
    // exactly what an operator debugging a flaky subscriber would see.
    var rejected = failureRate > 0 && random.NextDouble() < failureRate;

    store.Record(new Receipt(
        DateTimeOffset.UtcNow,
        request.Path,
        eventType,
        webhookId,
        signatureValid,
        rejected ? "REJECTED (503)" : "ACCEPTED (204)",
        Truncate(payload, 500)));

    if (!signatureValid)
    {
        return Results.Json(new { error = "invalid_signature" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (rejected)
    {
        return Results.Problem("Subscriber temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.NoContent();
}

// Verifies a Standard Webhooks signature: HMAC-SHA256 over "{id}.{timestamp}.{payload}", compared against
// the base64 in the "v1,<base64>" webhook-signature header. Unsigned deliveries (Signing = None, no
// headers) are treated as valid — the host may embed its own signature in the payload instead.
static bool VerifySignature(HttpRequest request, string payload, string secret)
{
    if (!request.Headers.TryGetValue("webhook-signature", out var signatureHeader))
    {
        return true;
    }

    if (!request.Headers.TryGetValue("webhook-id", out var id)
        || !request.Headers.TryGetValue("webhook-timestamp", out var timestamp))
    {
        return false;
    }

    var key = DecodeSecret(secret);
    var signedContent = $"{id}.{timestamp}.{payload}";
    using var hmac = new HMACSHA256(key);
    var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedContent)));

    foreach (var part in signatureHeader.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var value = part.StartsWith("v1,", StringComparison.Ordinal) ? part[3..] : part;
        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(value),
                Encoding.UTF8.GetBytes(expected)))
        {
            return true;
        }
    }

    return false;
}

static byte[] DecodeSecret(string secret)
{
    var encoded = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;

    return Convert.FromBase64String(encoded);
}

static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

namespace Warp.Demo.PartnerApi
{
    internal sealed record ChargeRequest(string Provider, string OrderId, decimal Amount, string? Channel);

    internal sealed record ChargeResult(string PaymentId, string OrderId, decimal Amount, string Status);

    internal sealed record RefundRequest(string PaymentId);

    internal sealed record RateQuote(string Sku, string Carrier, decimal Price);

    internal sealed record ShipmentRequest(string OrderId, string? Carrier, string Sku, string? Channel);

    internal sealed record ShipmentResult(string TrackingNumber, string OrderId, string Carrier, string Status);

    internal sealed record Receipt(
        DateTimeOffset At,
        string Path,
        string EventType,
        string? WebhookId,
        bool SignatureValid,
        string Outcome,
        string Payload);

    /// <summary>Thread-safe, bounded in-memory log of received webhook attempts for the demo viewer.</summary>
    internal sealed class ReceiptStore
    {
        private const int Capacity = 200;
        private readonly ConcurrentQueue<Receipt> _receipts = new();

        public void Record(Receipt receipt)
        {
            _receipts.Enqueue(receipt);
            while (_receipts.Count > Capacity && _receipts.TryDequeue(out _))
            {
            }
        }

        public IReadOnlyList<Receipt> Snapshot() => [.. _receipts.Reverse()];

        public string RenderHtml()
        {
            var rows = new StringBuilder();
            foreach (var r in _receipts.Reverse())
            {
                var sig = r.SignatureValid ? "✓" : "✗ invalid";
                rows.Append(CultureInfo.InvariantCulture, $"<tr><td>{r.At:HH:mm:ss}</td><td>{r.Path}</td><td>{r.EventType}</td><td>{r.WebhookId}</td><td>{sig}</td><td>{r.Outcome}</td></tr>");
            }

            return $$"""
                <!doctype html>
                <html><head><title>Subscriber — received shop webhooks</title>
                <meta http-equiv="refresh" content="2">
                <style>body{font-family:system-ui,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}
                th,td{border:1px solid #ddd;padding:6px 10px;text-align:left;font-size:14px}th{background:#f4f4f5}</style>
                </head><body>
                <h1>Subscriber — received shop webhooks</h1>
                <p>Auto-refreshes every 2s. Newest first. Total: {{_receipts.Count}}.</p>
                <table><thead><tr><th>Time</th><th>Path</th><th>Event</th><th>webhook-id</th><th>Signature</th><th>Outcome</th></tr></thead>
                <tbody>{{rows}}</tbody></table>
                </body></html>
                """;
        }
    }
}
