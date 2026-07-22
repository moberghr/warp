using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Signing coverage (WSC6), NoDb. Proves the built-in <see cref="StandardWebhooksSigner"/> matches the
/// published Standard Webhooks test vector and emits exactly the three <c>webhook-*</c> headers with the
/// stable <c>webhook-id</c>; that <c>WebhookSigning.None</c> is the send default and wires no
/// <see cref="IWebhookSigner"/>; and that <c>WebhookSigning.Custom</c> resolves the host's registered
/// signer end-to-end through the real <c>AddWebhooks</c> DI path — with a missing custom signer failing at
/// <c>AddWebhooks</c> registration time, never at send time.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookSigningTests
{
    // Published Standard Webhooks test vector — https://github.com/standard-webhooks/standard-webhooks
    // (README / libraries/javascript spec vector). Verified independently (HMAC-SHA256 over
    // "{id}.{ts}.{payload}" with the base64-decoded secret) before being pinned here.
    private const string VectorSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string VectorWebhookId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long VectorUnixSeconds = 1614265330;
    private const string VectorPayload = "{\"test\": 2432232314}";
    private const string VectorSignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    [TimedFact]
    public void StandardWebhooksSigner_KnownVector_MatchesPublishedSignature()
    {
        var signer = new StandardWebhooksSigner();

        var headers = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = VectorWebhookId,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(VectorUnixSeconds),
            Payload = VectorPayload,
            Secret = VectorSecret,
        });

        headers["webhook-id"].ShouldBe(VectorWebhookId);
        headers["webhook-timestamp"].ShouldBe(VectorUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        headers["webhook-signature"].ShouldBe(VectorSignature);
    }

    [TimedFact]
    public void StandardWebhooksSigner_EmitsExactlyTheThreeWebhookHeaders()
    {
        var signer = new StandardWebhooksSigner();

        var headers = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = VectorWebhookId,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(VectorUnixSeconds),
            Payload = VectorPayload,
            Secret = VectorSecret,
        });

        headers.Keys.OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(["webhook-id", "webhook-signature", "webhook-timestamp"]);
    }

    [TimedFact]
    public void StandardWebhooksSigner_WebhookIdIsEventId_StableAcrossTimestamps()
    {
        var signer = new StandardWebhooksSigner();

        var first = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = VectorWebhookId,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(VectorUnixSeconds),
            Payload = VectorPayload,
            Secret = VectorSecret,
        });

        var retry = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = VectorWebhookId,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(VectorUnixSeconds + 3600),
            Payload = VectorPayload,
            Secret = VectorSecret,
        });

        // webhook-id (= EventId) is the consumer's idempotency key: constant across retries.
        retry["webhook-id"].ShouldBe(first["webhook-id"]);

        // A later attempt re-signs with the new timestamp, so timestamp and signature move.
        retry["webhook-timestamp"].ShouldNotBe(first["webhook-timestamp"]);
        retry["webhook-signature"].ShouldNotBe(first["webhook-signature"]);
    }

    [TimedFact]
    public void StandardWebhooksSigner_MissingSecret_Throws()
    {
        var signer = new StandardWebhooksSigner();

        Should.Throw<InvalidOperationException>(() => signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = VectorWebhookId,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(VectorUnixSeconds),
            Payload = VectorPayload,
            Secret = null,
        }));
    }

    [TimedFact]
    public void AddWebhooks_Default_WiresNoCustomSigner()
    {
        // WebhookSigning.None (the send default) adds no headers and selects no signer. AddWarp registers
        // the built-in StandardWebhooksSigner because the engine is part of Core (§8.20), so AddWebhooks with
        // no options must wire no custom IWebhookSigner of its own.
        new WebhookSend { Url = "https://example.test", EventType = "e" }.Signing.ShouldBe(WebhookSigning.None);

        var services = new ServiceCollection();
        new WarpBuilder<TestContext>(services).AddWebhooks();

        services.Any(x => x.ServiceType == typeof(IWebhookSigner)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task AddWebhooks_UseCustomSignerGeneric_ResolvesRegisteredSignerEndToEnd()
    {
        var services = new ServiceCollection();
        new WarpBuilder<TestContext>(services).AddWebhooks(w => w.UseCustomSigner<StubCustomSigner>());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var signer = scope.ServiceProvider.GetRequiredService<IWebhookSigner>();

        signer.ShouldBeOfType<StubCustomSigner>();

        var headers = signer.Sign(new WebhookSignatureRequest
        {
            WebhookId = "evt-1",
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = "{}",
            Secret = null,
        });

        headers.ShouldContainKey("x-stub-signature");
    }

    [TimedFact]
    public void AddWebhooks_CustomSigningDeclaredWithoutSigner_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();

        // The host declared custom signing but wired no IWebhookSigner: fail now (registration), not at
        // send time when a WebhookSigning.Custom delivery would otherwise fault mid-flight.
        Should.Throw<InvalidOperationException>(
            () => new WarpBuilder<TestContext>(services).AddWebhooks(w => w.UseCustomSigner()));
    }

    [TimedFact]
    public void AddWebhooks_CustomSigningDeclaredWithExternalSigner_Succeeds()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWebhookSigner, StubCustomSigner>();

        // Signer registered directly in DI before AddWebhooks: the declaration validates and does not throw.
        Should.NotThrow(() => new WarpBuilder<TestContext>(services).AddWebhooks(w => w.UseCustomSigner()));
    }
}

/// <summary>A minimal host-supplied <see cref="IWebhookSigner"/> for the Custom-signing wiring tests.</summary>
internal sealed class StubCustomSigner : IWebhookSigner
{
    public IReadOnlyDictionary<string, string> Sign(WebhookSignatureRequest request)
        => new Dictionary<string, string>(StringComparer.Ordinal) { ["x-stub-signature"] = "signed" };
}
