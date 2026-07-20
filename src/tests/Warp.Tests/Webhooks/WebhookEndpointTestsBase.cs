using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Adapters.Webhooks;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Dashboard-backend coverage for the Webhooks feature (WSC8). Every data assertion drives the real
/// <c>WarpEndpoints</c> HTTP routes through <see cref="TestServer"/> (route templates, query binding, JSON
/// serialization, 404 mapping) — not the query/command service in isolation (adapters lesson TR2). The
/// query/command services are backed by the fixture database; the <c>GET /api/addons</c> <c>webhooks</c>
/// flag is exercised in both registration shapes (with and without the <see cref="IWebhookRedeliveryEnqueuer"/>
/// marker). Each test drives exactly one endpoint (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookEndpointTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WebhookEndpointTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void AddWebhooks_RegistersRedeliveryEnqueuerMarker()
    {
        // The addons flag + the redelivery enqueue are gated on IWebhookRedeliveryEnqueuer — only
        // AddWebhooks() registers it. Drives the real ServiceCollection + builder path (adapters lesson).
        var services = new ServiceCollection();

        new WarpBuilder<TestContext>(services).AddWebhooks();

        services.Any(x => x.ServiceType == typeof(IWebhookRedeliveryEnqueuer)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetWebhooks_ReturnsDeliveries()
    {
        await SeedDeliveryAsync("order.created", WebhookDeliveryStatus.Pending);
        await SeedDeliveryAsync("order.shipped", WebhookDeliveryStatus.Delivered);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var list = await client.GetFromJsonAsync<List<WebhookDeliveryListItem>>("/warp/api/webhooks", Ct);

            list.ShouldNotBeNull();
            list.Count.ShouldBe(2);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetWebhooks_FilterByStatus_ReturnsMatchingOnly()
    {
        await SeedDeliveryAsync("order.created", WebhookDeliveryStatus.Delivered);
        await SeedDeliveryAsync("order.shipped", WebhookDeliveryStatus.Exhausted);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var list = await client.GetFromJsonAsync<List<WebhookDeliveryListItem>>(
                $"/warp/api/webhooks?status={(int)WebhookDeliveryStatus.Exhausted}",
                Ct);

            var item = list.ShouldNotBeNull().ShouldHaveSingleItem();
            item.EventType.ShouldBe("order.shipped");
            item.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetWebhookDetail_KnownId_ReturnsContractWithRedactedSecretAndAttemptTimeline()
    {
        var deliveryId = await SeedDeliveryAsync(
            "order.created",
            WebhookDeliveryStatus.Delivered,
            secret: "whsec_supersecret",
            headersJson: "{\"Authorization\":\"Bearer top-secret\",\"X-Trace\":\"abc\"}");

        await SeedAttemptAsync(deliveryId, AdapterCallOutcome.Failed, statusCode: 500, timestamp: DateTime.UtcNow.AddSeconds(-2));
        await SeedAttemptAsync(deliveryId, AdapterCallOutcome.Success, statusCode: 200, timestamp: DateTime.UtcNow);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var detail = await client.GetFromJsonAsync<WebhookDeliveryDetail>($"/warp/api/webhooks/{deliveryId}", Ct);

            detail.ShouldNotBeNull();
            detail.Id.ShouldBe(deliveryId);

            // Secret never leaves the service — only the HasSecret flag does.
            detail.HasSecret.ShouldBeTrue();

            // Authorization-class headers are redacted on the read surface (§1.2).
            detail.HeadersJson.ShouldNotBeNull();
            detail.HeadersJson.ShouldNotContain("top-secret");
            detail.HeadersJson.ShouldContain("***");

            // Attempt timeline assembled from AdapterCallLog by CorrelationId, oldest first.
            detail.Attempts.Count.ShouldBe(2);
            detail.Attempts[0].Outcome.ShouldBe(AdapterCallOutcome.Failed);
            detail.Attempts[1].Outcome.ShouldBe(AdapterCallOutcome.Success);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetWebhookDetail_UnknownId_Returns404()
    {
        var (app, client) = await CreateEndpointHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/webhooks/{Guid.NewGuid()}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RedeliverEndpoint_SettledDelivery_Returns200AndResetsToPending()
    {
        var deliveryId = await SeedDeliveryAsync("order.created", WebhookDeliveryStatus.Exhausted, attemptCount: 4);

        // A redelivery enqueuer is registered (server-host shape), so the settled delivery flips to Pending.
        var (app, client) = await CreateEndpointHost(registerEnqueuer: true);
        try
        {
            var response = await client.PostAsync($"/warp/api/webhooks/{deliveryId}/redeliver", content: null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var delivery = await _fixture.CreateContext().Set<WebhookDelivery>()
                .AsNoTracking()
                .Where(x => x.Id == deliveryId)
                .FirstAsync(Ct);
            delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
            delivery.AttemptCount.ShouldBe(0);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RedeliverEndpoint_PendingDelivery_Returns409()
    {
        var deliveryId = await SeedDeliveryAsync("order.created", WebhookDeliveryStatus.Pending);

        var (app, client) = await CreateEndpointHost(registerEnqueuer: true);
        try
        {
            var response = await client.PostAsync($"/warp/api/webhooks/{deliveryId}/redeliver", content: null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RedeliverEndpoint_NoEnqueuer_Returns409AndLeavesRowUntouched()
    {
        var deliveryId = await SeedDeliveryAsync("order.created", WebhookDeliveryStatus.Exhausted, attemptCount: 4);

        // No enqueuer registered (dashboard-only shape): redeliver must reject, not strand the row Pending.
        var (app, client) = await CreateEndpointHost(registerEnqueuer: false);
        try
        {
            var response = await client.PostAsync($"/warp/api/webhooks/{deliveryId}/redeliver", content: null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            var delivery = await _fixture.CreateContext().Set<WebhookDelivery>()
                .AsNoTracking()
                .Where(x => x.Id == deliveryId)
                .FirstAsync(Ct);
            delivery.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
            delivery.AttemptCount.ShouldBe(4);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RedeliverEndpoint_UnknownId_Returns404()
    {
        var (app, client) = await CreateEndpointHost(registerEnqueuer: true);
        try
        {
            var response = await client.PostAsync($"/warp/api/webhooks/{Guid.NewGuid()}/redeliver", content: null, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetWebhooksSummary_ReturnsPerStatusCounts()
    {
        await SeedDeliveryAsync("a", WebhookDeliveryStatus.Pending);
        await SeedDeliveryAsync("b", WebhookDeliveryStatus.Delivered);
        await SeedDeliveryAsync("c", WebhookDeliveryStatus.Delivered);
        await SeedDeliveryAsync("d", WebhookDeliveryStatus.Exhausted);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var summary = await client.GetFromJsonAsync<WebhookDeliverySummary>("/warp/api/webhooks/summary", Ct);

            summary.ShouldNotBeNull();
            summary.Total.ShouldBe(4);
            summary.Pending.ShouldBe(1);
            summary.Delivered.ShouldBe(2);
            summary.Exhausted.ShouldBe(1);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_WebhooksRegistered_FlagTrue()
    {
        var (app, client) = await CreateAddonsHost(registerWebhooks: true);
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", Ct);

            info.ShouldNotBeNull();
            info.Webhooks.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_WebhooksNotRegistered_FlagFalse()
    {
        var (app, client) = await CreateAddonsHost(registerWebhooks: false);
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", Ct);

            info.ShouldNotBeNull();
            info.Webhooks.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private async Task<(WebApplication App, HttpClient Client)> CreateEndpointHost(bool registerEnqueuer = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        // Back the always-registered query/command services with the fixture database so the real route
        // templates, binding, and JSON serialization in WarpEndpoints are exercised end-to-end. The
        // IWebhookRedeliveryEnqueuer presence (server-host shape) vs absence (dashboard-only shape) drives
        // the redeliver status mapping the endpoint tests cover.
        var fixture = _fixture;
        var enqueuers = registerEnqueuer
            ? new[] { Mock.Of<IWebhookRedeliveryEnqueuer>() }
            : [];
        builder.Services.AddScoped<IWebhookQueryService>(_ => new WebhookQueryService<TestContext>(fixture.CreateContext()));
        builder.Services.AddScoped<IWebhookCommandService>(_ => new WebhookCommandService<TestContext>(
            fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            enqueuers));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAddonsHost(bool registerWebhooks)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        if (registerWebhooks)
        {
            builder.Services.AddScoped(_ => Mock.Of<IWebhookRedeliveryEnqueuer>());
        }

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    private async Task<Guid> SeedDeliveryAsync(
        string eventType,
        WebhookDeliveryStatus status,
        int attemptCount = 0,
        string? secret = null,
        string? headersJson = null)
    {
        var ctx = _fixture.CreateContext();
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            HeadersJson = headersJson,
            PayloadJson = "{\"order\":42}",
            SigningMode = WebhookSigning.None,
            Secret = secret,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            AttemptCount = attemptCount,
            CreatedAt = DateTime.UtcNow,
        };

        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private async Task SeedAttemptAsync(Guid deliveryId, AdapterCallOutcome outcome, int statusCode, DateTime timestamp)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<AdapterCallLog>().Add(new AdapterCallLog
        {
            AdapterName = "warp-webhooks",
            Operation = "order.created",
            Timestamp = timestamp,
            DurationMs = 12,
            Attempts = 1,
            Outcome = outcome,
            StatusCode = statusCode,
            CorrelationId = deliveryId.ToString(),
            MachineName = "test-host",
        });

        await ctx.SaveChangesAsync(Ct);
    }
}
