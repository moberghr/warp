using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Adapters.Webhooks;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Validation coverage for <see cref="WebhookDispatcher{TContext}.SendAsync"/> (W-3): the single build
/// choke point rejects inputs that would otherwise fault deep in the executor (empty timeline) or silently
/// change behaviour (a truncated URL points elsewhere; an invalid signing secret produces a bad signature).
/// The publisher is a no-op mock — every case throws before any row is staged, so nothing is persisted.
/// Each test drives exactly one <c>SendAsync</c> call (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookDispatcherValidationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WebhookDispatcherValidationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task SendAsync_RelativeUrl_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(new WebhookSend { Url = "/relative/path", EventType = "order.created" }, Ct));
    }

    [TimedFact]
    public async Task SendAsync_NonHttpScheme_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(new WebhookSend { Url = "ftp://example.test/hook", EventType = "order.created" }, Ct));
    }

    [TimedFact]
    public async Task SendAsync_UrlOverCap_Throws()
    {
        var longUrl = "https://example.test/" + new string('a', 2100);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(new WebhookSend { Url = longUrl, EventType = "order.created" }, Ct));
    }

    [TimedFact]
    public async Task SendAsync_NegativeScheduleEntry_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(
                new WebhookSend
                {
                    Url = "https://example.test/hook",
                    EventType = "order.created",
                    RetrySchedule = [TimeSpan.FromMinutes(-1)],
                },
                Ct));
    }

    [TimedFact]
    public async Task SendAsync_StandardWebhooksWithoutSecret_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(
                new WebhookSend
                {
                    Url = "https://example.test/hook",
                    EventType = "order.created",
                    Signing = WebhookSigning.StandardWebhooks,
                },
                Ct));
    }

    [TimedFact]
    public async Task SendAsync_StandardWebhooksWithNonBase64Secret_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(
                new WebhookSend
                {
                    Url = "https://example.test/hook",
                    EventType = "order.created",
                    Signing = WebhookSigning.StandardWebhooks,
                    Secret = "whsec_not valid base64!!!",
                },
                Ct));
    }

    [TimedFact]
    public async Task SendAsync_SecretOverCap_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(
                new WebhookSend
                {
                    Url = "https://example.test/hook",
                    EventType = "order.created",
                    Secret = new string('a', 513),
                },
                Ct));
    }

    [TimedFact]
    public async Task SendAsync_BlankUrl_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(new WebhookSend { Url = "   ", EventType = "order.created" }, Ct));
    }

    [TimedFact]
    public async Task SendAsync_BlankEventType_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateDispatcher().SendAsync(new WebhookSend { Url = "https://example.test/hook", EventType = "   " }, Ct));
    }

    [TimedFact]
    public async Task SendAsync_WhitespaceEventId_ReplacedWithGeneratedId()
    {
        // EventId is the stable idempotency key and the Standard Webhooks webhook-id header value — a
        // whitespace-only caller value is as useless as an empty one and must be replaced with a fresh id,
        // not persisted verbatim.
        var ctx = _fixture.CreateContext();
        var dispatcher = new WebhookDispatcher<TestContext>(
            ctx,
            Mock.Of<IPublisher>(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()));

        var id = await dispatcher.SendAsync(
            new WebhookSend
            {
                Url = "https://example.test/hook",
                EventType = "order.created",
                EventId = "   ",
            },
            Ct);

        await ctx.SaveChangesAsync(Ct);

        var row = await _fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);

        Guid.TryParse(row.EventId, out _).ShouldBeTrue("a whitespace EventId must be replaced with a generated GUID");
    }

    [TimedFact]
    public async Task SendAsync_OverLongDisplayFields_SilentlyClampedToCaps_AndRowPersists()
    {
        // The clamp contract (distinct from the throw contract above): over-long DISPLAY fields
        // (EventType/GroupName/Reference) are silently truncated to their column caps and the row is built —
        // truncating those is lossy but harmless. URL and Secret THROW instead (proven above) because a
        // truncated URL/secret would silently change behaviour.
        var ctx = _fixture.CreateContext();
        var dispatcher = new WebhookDispatcher<TestContext>(
            ctx,
            Mock.Of<IPublisher>(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()));

        var id = await dispatcher.SendAsync(
            new WebhookSend
            {
                Url = "https://example.test/hook",
                EventType = new string('e', 250),
                Group = new string('g', 250),
                Reference = new string('r', 250),
            },
            Ct);

        // The no-op mock publisher never commits the shared context, so flush the tracked (Added) delivery
        // here to prove the built row persists with clamped values.
        await ctx.SaveChangesAsync(Ct);

        var row = await _fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);

        row.EventType.Length.ShouldBe(200);
        row.GroupName.ShouldNotBeNull().Length.ShouldBe(200);
        row.Reference.ShouldNotBeNull().Length.ShouldBe(200);
    }

    private WebhookDispatcher<TestContext> CreateDispatcher()
        => new(
            _fixture.CreateContext(),
            Mock.Of<IPublisher>(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()));
}
