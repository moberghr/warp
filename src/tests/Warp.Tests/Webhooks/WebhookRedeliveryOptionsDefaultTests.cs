using Shouldly;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// The default interface implementation of
/// <see cref="IWebhookCommandService.Redeliver(Guid, WebhookRedeliveryOptions, CancellationToken)"/> keeps
/// existing implementers compiling: without a secret it forwards to the original overload, with one it
/// refuses rather than redelivering signed with the old key.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookRedeliveryOptionsDefaultTests
{
    [TimedFact]
    public async Task Redeliver_LegacyImplementerWithoutSecret_ForwardsToOriginalOverload()
    {
        var legacy = new LegacyCommandService();
        IWebhookCommandService service = legacy;

        var result = await service.Redeliver(Guid.NewGuid(), new WebhookRedeliveryOptions(), Xunit.TestContext.Current.CancellationToken);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);
        legacy.Calls.ShouldBe(1);
    }

    [TimedFact]
    public async Task Redeliver_LegacyImplementerWithSecret_ThrowsNotSupported()
    {
        var legacy = new LegacyCommandService();
        IWebhookCommandService service = legacy;

        await Should.ThrowAsync<NotSupportedException>(() =>
            service.Redeliver(Guid.NewGuid(), new WebhookRedeliveryOptions { Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw" }, Xunit.TestContext.Current.CancellationToken));

        legacy.Calls.ShouldBe(0);
    }

    // Implements only the member that existed before 8.1, like a host decorator or test double written then.
    private sealed class LegacyCommandService : IWebhookCommandService
    {
        public int Calls { get; private set; }

        public Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, CancellationToken ct = default)
        {
            Calls++;

            return Task.FromResult(WebhookRedeliveryResult.Enqueued);
        }
    }
}
