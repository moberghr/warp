using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Webhooks;
using Warp.Http;
using Warp.Http.Observability;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;
using Warp.Worker;

namespace Warp.Tests.EndToEnd;

/// <summary>
/// The one full-stack smoke test: boots a single Warp app that is worker + web host + dashboard API on the
/// real fixture DB, then exercises ALL four observability surfaces at once and reads them back through the
/// dashboard API the UI consumes. A job runs (JobLog), its handler makes an outbound adapter call
/// (AdapterCallLog), a webhook is delivered (WebhookDelivery + attempt AdapterCallLog), and inbound requests
/// hit a Warp HTTP endpoint (EndpointCallLog). All outbound/webhook traffic loops back to the app's own
/// endpoint via the in-memory TestServer, so it is self-contained. This is the "does it all wire together"
/// signal — one green/red across jobs, adapters, webhooks, endpoints, and the dashboard.
/// </summary>
[GenerateDatabaseTests]
public abstract class FullStackObservabilityTestsBase : IAsyncLifetime
{
    private const string AdapterName = "loopback-vendor";

    private readonly IDatabaseFixture _fixture;

    protected FullStackObservabilityTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact(30_000)]
    public async Task FullStack_JobAdapterWebhookAndInbound_AllRecordedAndVisibleInDashboard()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        // 1) Inbound: hit the Warp HTTP endpoint directly → EndpointCallLog.
        (await client.PostAsync("/loopback", EmptyJson(), Ct)).EnsureSuccessStatusCode();

        // 2) Job: enqueue a job whose handler logs (JobLog) and makes an outbound adapter call to the
        //    loopback endpoint (AdapterCallLog outbound + another EndpointCallLog inbound).
        Guid jobId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            jobId = await publisher.Enqueue(new LoopbackCallerJob());
            await publisher.SaveChangesAsync(Ct);
        }

        // 3) Webhook: deliver to the loopback endpoint → WebhookDelivery(Delivered) + attempt AdapterCallLog.
        Guid deliveryId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();
            deliveryId = await dispatcher.SendAsync(
                new WebhookSend
                {
                    Url = "http://localhost/loopback",
                    EventType = "order.created",
                    Group = "loopback-endpoint",
                    Payload = "{\"hello\":\"world\"}",
                    RetrySchedule = [],
                },
                Ct);
        }

        // Wait for the job to finish and the webhook to settle.
        await WaitUntil(async ctx => await ctx.Set<Job>().AnyAsync(x => x.Id == jobId && x.CurrentState == State.Completed, Ct));
        await WaitUntil(async ctx => await ctx.Set<WebhookDelivery>().AnyAsync(x => x.Id == deliveryId && x.Status == WebhookDeliveryStatus.Delivered, Ct));

        // ---- Assert the raw log rows landed (flushers drain the bounded channels onto TContext). ----

        // JobLog: the handler's Information log is captured onto the job.
        await WaitUntil(async ctx => await ctx.Set<JobLog>().AnyAsync(x => x.JobId == jobId, Ct));

        // AdapterCallLog: the outbound vendor call AND the webhook attempt (recorded under warp-webhooks).
        await WaitUntil(async ctx => await ctx.Set<AdapterCallLog>().AnyAsync(x => x.AdapterName == AdapterName, Ct));
        await WaitUntil(async ctx => await ctx.Set<AdapterCallLog>()
            .AnyAsync(x => x.AdapterName == WebhookConstants.AdapterName && x.CorrelationId == deliveryId.ToString(), Ct));

        // EndpointCallLog: the inbound requests to the Warp HTTP endpoint (direct hit + the looped-back
        // adapter/webhook calls all target /loopback).
        await WaitUntil(async ctx => await ctx.Set<EndpointCallLog>().AnyAsync(x => x.RouteTemplate == "/loopback", Ct));

        // ---- Assert the dashboard API surfaces the same data (the JSON the UI reads). ----

        // /api/addons reports the enabled surfaces.
        var addons = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", Ct);
        addons.ShouldNotBeNull();
        addons.Adapters.ShouldBeTrue();
        addons.Endpoints.ShouldBeTrue();
        addons.Webhooks.ShouldBeTrue();

        // Adapters dashboard lists the vendor adapter (and the webhooks adapter).
        var adapters = await client.GetStringAsync("/warp/api/adapters", Ct);
        adapters.ShouldContain(AdapterName);

        // Endpoints dashboard lists the /loopback route.
        var endpoints = await client.GetStringAsync("/warp/api/endpoints", Ct);
        endpoints.ShouldContain("/loopback");

        // Webhooks dashboard lists the delivery.
        var webhooks = await client.GetStringAsync("/warp/api/webhooks", Ct);
        webhooks.ShouldContain(deliveryId.ToString());

        await app.StopAsync(Ct);
    }

    private static StringContent EmptyJson() => new("{}", System.Text.Encoding.UTF8, "application/json");

    private async Task WaitUntil(Func<TestContext, Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var ctx = _fixture.CreateContext();
            if (await predicate(ctx))
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Condition not met within 20s.");
    }

    private async Task<WebApplication> StartAppAsync()
    {
        var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        await probe.DisposeAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.Services.AddDbContext<TestContext>(options =>
        {
            if (isPostgres)
            {
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });

        builder.Services.AddRouting();
        builder.Services.AddWarpHttp();

        builder.Services.AddWarpServer<TestContext>(opt =>
        {
            if (isPostgres)
            {
                opt.UsePostgreSql();
            }
            else
            {
                opt.UseSqlServer();
            }

            // One worker is all this test needs (drain one job + one webhook executor). Left at the default
            // (min(cores*5, 20) = up to 20) it would hammer the SHARED test-container every 100ms in parallel
            // with the whole PG suite — a load spike that destabilizes timing-sensitive neighbours. Integration
            // tests that boot a full worker host must be good neighbours (WarpTestServer caps itself the same way).
            opt.WorkerCount = 1;
            opt.PollingInterval = TimeSpan.FromMilliseconds(100);
            opt.MaxPollingInterval = TimeSpan.FromMilliseconds(100);
            opt.PollingIntervalFactor = 1.0;
            opt.ScheduledActivationInterval = TimeSpan.FromMilliseconds(250);
            opt.CounterAggregationInterval = null;

            opt.AddAdapters();
            opt.AddEndpointObservability(o => o.CaptureResponseBodies = CaptureMode.Always);
            opt.AddAdapter(AdapterName, a => a.BaseUrl = new Uri("http://localhost/"));
        });

        // Loopback: route the vendor adapter client and the warp-webhooks client back into this app's own
        // in-memory TestServer, so the outbound call and the webhook delivery hit /loopback here. Resolved
        // lazily (per client creation, after the app has started) so the TestServer exists.
        builder.Services.AddHttpClient(AdapterName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());
        builder.Services.AddHttpClient(WebhookConstants.AdapterName)
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());

        var app = builder.Build();

        app.UseRouting();
        app.UseWarpHttpObservability();
        app.MapWarpHttp();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(Ct);

        return app;
    }
}

/// <summary>Inbound Warp HTTP endpoint used as the self-contained loopback target for the full-stack test.</summary>
public sealed record LoopbackRequest : IRequest<IResult>;

[WarpHttpPost("/loopback")]
public sealed class LoopbackHttpHandler : IRequestHandler<LoopbackRequest, IResult>
{
    public Task<IResult> HandleAsync(LoopbackRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Results.Ok(new { ok = true }));
}

/// <summary>A job whose handler logs (→ JobLog) and makes one outbound adapter call (→ AdapterCallLog).</summary>
public sealed class LoopbackCallerJob : IJob;

public sealed class LoopbackCallerJobHandler : IJobHandler<LoopbackCallerJob>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LoopbackCallerJobHandler> _logger;

    public LoopbackCallerJobHandler(IHttpClientFactory httpClientFactory, ILogger<LoopbackCallerJobHandler> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task HandleAsync(LoopbackCallerJob message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Full-stack job calling the loopback vendor adapter.");

        var client = _httpClientFactory.CreateClient("loopback-vendor");
        using var response = await client.PostAsync("/loopback", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
