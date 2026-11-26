using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// The 8.1 dashboard routes through the real <c>WarpEndpoints</c> (binding, status mapping, JSON): cancel one
/// delivery, cancel a group, "Retry now" for a failing endpoint, and the endpoint health and #357 fields on the reads.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookEndpointHealthRouteTestsBase : IAsyncLifetime
{
    private const string Group = "acme";

    private readonly IDatabaseFixture _fixture;

    protected WebhookEndpointHealthRouteTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedTheory]
    [InlineData(WebhookDeliveryStatus.Pending, HttpStatusCode.OK)]
    [InlineData(WebhookDeliveryStatus.Delivered, HttpStatusCode.Conflict)]
    public async Task PostCancel_MapsTheResult(WebhookDeliveryStatus status, HttpStatusCode expected)
    {
        var id = await SeedDeliveryAsync(status);
        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync($"/warp/api/webhooks/{id}/cancel", null, Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [TimedFact]
    public async Task PostCancel_UnknownId_Returns404()
    {
        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync($"/warp/api/webhooks/{Guid.NewGuid()}/cancel", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task PostCancelGroup_ReturnsTheCount()
    {
        await SeedDeliveryAsync(WebhookDeliveryStatus.Parked);
        await SeedDeliveryAsync(WebhookDeliveryStatus.Pending);
        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync($"/warp/api/webhooks/groups/cancel?group={Group}", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("cancelled").GetInt32().ShouldBe(2);
    }

    [TimedFact]
    public async Task PostCancelGroup_WithoutGroup_Returns400()
    {
        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync("/warp/api/webhooks/groups/cancel", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedTheory]
    [InlineData(true, true, HttpStatusCode.OK)]
    [InlineData(true, false, HttpStatusCode.Conflict)]
    [InlineData(false, false, HttpStatusCode.NotFound)]
    public async Task PostRetry_MapsTheProbeResult(bool failingEndpoint, bool parked, HttpStatusCode expected)
    {
        if (failingEndpoint)
        {
            await SeedEndpointAsync();
        }

        if (parked)
        {
            await SeedDeliveryAsync(WebhookDeliveryStatus.Parked);
        }

        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync($"/warp/api/webhooks/groups/retry?group={Group}", null, Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [TimedFact]
    public async Task PostRetry_NothingParked_CarriesAReasonCode()
    {
        await SeedEndpointAsync();
        await using var host = await HostAsync();

        using var response = await host.Client.PostAsync($"/warp/api/webhooks/groups/retry?group={Group}", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("reason").GetString().ShouldBe("nothing-parked");
    }

    [TimedFact]
    public async Task GetGroupsByEndpoint_CarriesHealthAndCounts()
    {
        await SeedEndpointAsync();
        await SeedDeliveryAsync(WebhookDeliveryStatus.Parked);
        await SeedDeliveryAsync(WebhookDeliveryStatus.Delivered, lastAttemptAfter: TimeSpan.FromSeconds(2));
        await SeedDeliveryAsync(WebhookDeliveryStatus.Cancelled);
        await using var host = await HostAsync();

        var groups = await host.Client.GetFromJsonAsync<List<WebhookGroupModel>>("/warp/api/webhooks/groups?by=endpoint", Ct);

        var row = groups.ShouldNotBeNull().ShouldHaveSingleItem();
        row.Key.ShouldBe(Group);
        row.Health.ShouldBe(WebhookEndpointState.Failing);
        row.FailingSince.ShouldNotBeNull();
        row.Parked.ShouldBe(1);
        row.Cancelled.ShouldBe(1);
        row.Delivered.ShouldBe(1);
        row.SuccessRate.ShouldBe(0.5);
        row.P95DeliveryMs.ShouldNotBeNull().ShouldBe(2000, 50);
        row.LastSuccessAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task GetDelivery_CarriesTheLastAttemptFields()
    {
        var id = await SeedDeliveryAsync(WebhookDeliveryStatus.Exhausted, lastAttemptAfter: TimeSpan.FromSeconds(1), lastStatusCode: 503, reason: WebhookFailureReason.UnsuccessfulStatus);
        await using var host = await HostAsync();

        var detail = await host.Client.GetFromJsonAsync<WebhookDeliveryDetail>($"/warp/api/webhooks/{id}", Ct);

        detail.ShouldNotBeNull();
        detail.LastStatusCode.ShouldBe(503);
        detail.LastFailureReason.ShouldBe(WebhookFailureReason.UnsuccessfulStatus);
        detail.LastAttemptAt.ShouldNotBeNull();
    }

    private async Task<EndpointHost> HostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        var fixture = _fixture;
        builder.Services.AddScoped<IWebhookQueryService>(_ => new WebhookQueryService<TestContext>(fixture.CreateContext()));
        builder.Services.AddScoped<IWebhookCommandService>(_ => new WebhookCommandService<TestContext>(
            fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            [new NoOpRedeliveryEnqueuer()]));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return new EndpointHost(app, app.GetTestClient());
    }

    private async Task SeedEndpointAsync()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookEndpoint>().Add(new WebhookEndpoint
        {
            Name = Group,
            State = WebhookEndpointState.Failing,
            FailingSince = DateTime.UtcNow.AddHours(-1),
            NextProbeAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedDeliveryAsync(
        WebhookDeliveryStatus status,
        TimeSpan? lastAttemptAfter = null,
        int? lastStatusCode = null,
        WebhookFailureReason? reason = null)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            GroupName = Group,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = status,
            AttemptCount = 1,
            CreatedAt = createdAt,
            LastAttemptAt = createdAt + (lastAttemptAfter ?? TimeSpan.FromSeconds(1)),
            LastStatusCode = lastStatusCode,
            LastFailureReason = reason ?? (status is WebhookDeliveryStatus.Parked or WebhookDeliveryStatus.Exhausted ? WebhookFailureReason.Timeout : null),
        };

        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private sealed class EndpointHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
