using System.Net;
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
using Warp.Core.Services;
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
/// real fixture DB, exercises ALL four observability surfaces at once with fully-controlled inputs, and then
/// verifies <b>every field</b> of every produced row — and the dashboard DTOs the UI reads — carries the
/// expected value. A job runs (JobLog), its handler makes an outbound adapter call to /vendor
/// (AdapterCallLog), a webhook is delivered to /hook (WebhookDelivery + attempt AdapterCallLog), and a direct
/// request hits /inbound (EndpointCallLog). All outbound/webhook traffic loops back into the app's own
/// in-memory TestServer, so it is self-contained. Distinct routes/operations/correlations make every row
/// uniquely identifiable so field values are deterministic (generated fields — ids, timestamps, durations,
/// machine name — get presence/range checks).
/// </summary>
[GenerateDatabaseTests]
public abstract class FullStackObservabilityTestsBase : IAsyncLifetime
{
    private const string VendorAdapter = "loopback-vendor";
    private const string VendorResponseBody = "vendor-response-payload";
    private const string HookResponseBody = "hook-response-payload";
    private const string InboundResponseBody = "inbound-response-payload";

    private readonly IDatabaseFixture _fixture;

    protected FullStackObservabilityTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact(30_000)]
    public async Task FullStack_EveryFieldOfEverySurface_IsCorrectAndVisibleInDashboard()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-1);
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        // ---- Drive: one direct inbound request, one job (→ outbound adapter call), one webhook. ----
        using (var inbound = new StringContent("{\"src\":\"inbound\"}", System.Text.Encoding.UTF8, "application/json"))
        {
            inbound.Headers.Add("X-Forwarded-For", "198.51.100.9");
            var req = new HttpRequestMessage(HttpMethod.Post, "/inbound") { Content = inbound };
            req.Headers.Add("X-Client", "acme-portal");
            req.Headers.UserAgent.ParseAdd("FullStackTest/1.0");
            (await client.SendAsync(req, Ct)).EnsureSuccessStatusCode();
        }

        Guid jobId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            jobId = await publisher.Enqueue(new LoopbackCallerJob());
            await publisher.SaveChangesAsync(Ct);
        }

        Guid deliveryId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();
            deliveryId = await dispatcher.SendAsync(
                new WebhookSend
                {
                    Url = "http://localhost/hook",
                    EventType = "order.created",
                    EventId = "evt-fullstack-1",
                    Group = "loopback-endpoint",
                    Reference = "ref-fullstack-1",
                    Payload = "{\"hello\":\"world\"}",
                    Signing = WebhookSigning.None,
                    RetrySchedule = [],
                },
                Ct);
        }

        await WaitUntil(async ctx => await ctx.Set<Job>().AnyAsync(x => x.Id == jobId && x.CurrentState == State.Completed, Ct));
        await WaitUntil(async ctx => await ctx.Set<WebhookDelivery>().AnyAsync(x => x.Id == deliveryId && x.Status == WebhookDeliveryStatus.Delivered, Ct));

        var now = DateTime.UtcNow.AddSeconds(1);

        // ============================ 1. JobLog (job ran + handler logged) ============================
        // The handler's ILogger output is captured to JobLog, stored as "[{category}] {message}".
        var expectedLog = $"[{typeof(LoopbackCallerJobHandler).FullName}] full-stack job pinged the vendor adapter";
        await WaitUntil(async ctx => await ctx.Set<JobLog>().AnyAsync(x => x.JobId == jobId && x.Message == expectedLog, Ct));
        await using (var ctx = _fixture.CreateContext())
        {
            var job = await ctx.Set<Job>().AsNoTracking().SingleAsync(x => x.Id == jobId, Ct);
            job.CurrentState.ShouldBe(State.Completed);
            job.Type!.ShouldContain(nameof(LoopbackCallerJob));

            var log = await ctx.Set<JobLog>().AsNoTracking().Where(x => x.JobId == jobId && x.Message == expectedLog).SingleAsync(Ct);
            log.Id.ShouldNotBe(Guid.Empty);
            log.JobId.ShouldBe(jobId);
            log.EventType.ShouldBe("Log");
            log.Level.ShouldBe("Information");
            log.Message.ShouldBe(expectedLog);
            log.Exception.ShouldBeNull();
            log.Timestamp.ShouldBeInRange(startedAt, now);
            log.WorkerId.ShouldNotBeNull();
            log.WorkerId!.Value.ShouldNotBe(Guid.Empty);
            log.DurationMs.ShouldBeNull();   // duration/name/value are for lifecycle+counter logs, not handler logs
            log.Name.ShouldBeNull();
            log.Value.ShouldBeNull();

            // The Completed lifecycle row (worker-written) — every column.
            var completed = await ctx.Set<JobLog>().AsNoTracking().Where(x => x.JobId == jobId && x.EventType == "Completed").SingleAsync(Ct);
            completed.Id.ShouldNotBe(Guid.Empty);
            completed.JobId.ShouldBe(jobId);
            completed.EventType.ShouldBe("Completed");
            completed.Level.ShouldBe("Information");
            completed.Message.ShouldBe($"Job {jobId} completed");
            completed.Exception.ShouldBeNull();
            completed.Timestamp.ShouldBeInRange(startedAt, now);
            completed.DurationMs.ShouldNotBeNull();
            completed.DurationMs!.Value.ShouldBeGreaterThanOrEqualTo(0);
            completed.WorkerId.ShouldNotBeNull();
            completed.Name.ShouldBeNull();
            completed.Value.ShouldBeNull();
        }

        // ==================== 2. AdapterCallLog — the outbound vendor call ====================
        await WaitUntil(async ctx => await ctx.Set<AdapterCallLog>().AnyAsync(x => x.AdapterName == VendorAdapter, Ct));
        await using (var ctx = _fixture.CreateContext())
        {
            var call = await ctx.Set<AdapterCallLog>().AsNoTracking().Where(x => x.AdapterName == VendorAdapter).SingleAsync(Ct);
            call.Id.ShouldNotBe(Guid.Empty);
            call.AdapterName.ShouldBe(VendorAdapter);
            call.Operation.ShouldBe("VendorPing");
            call.GroupName.ShouldBe("vendor-grp");
            call.Outcome.ShouldBe(AdapterCallOutcome.Success);
            call.StatusCode.ShouldBe(200);
            call.Attempts.ShouldBe(1);
            call.CorrelationId.ShouldBe("vendor-corr");
            call.ExceptionType.ShouldBeNull();
            call.ExceptionMessage.ShouldBeNull();
            call.RequestSummary.ShouldBe("POST http://localhost/vendor");
            call.RequestBody.ShouldBe("{\"src\":\"vendor\"}");
            call.ResponseBody.ShouldBe(VendorResponseBody);
            call.RequestHeaders.ShouldNotBeNullOrEmpty();
            call.ResponseHeaders.ShouldNotBeNullOrEmpty();
            call.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
            call.MachineName.ShouldBe(Environment.MachineName);
            call.Timestamp.ShouldBeInRange(startedAt, now);
            call.TagsJson.ShouldBeNull();
            call.TraceId.ShouldBeNull();   // no OTel listener in the test → no ambient trace id
            call.ExpireAt.ShouldNotBeNull();
            call.ExpireAt!.Value.ShouldBeGreaterThan(call.Timestamp);   // Timestamp + 7d global retention
        }

        // ============= 3. AdapterCallLog — the webhook attempt (warp-webhooks) =============
        await WaitUntil(async ctx => await ctx.Set<AdapterCallLog>().AnyAsync(x => x.AdapterName == WebhookConstants.AdapterName && x.CorrelationId == deliveryId.ToString(), Ct));
        await using (var ctx = _fixture.CreateContext())
        {
            var attempt = await ctx.Set<AdapterCallLog>().AsNoTracking()
                .Where(x => x.AdapterName == WebhookConstants.AdapterName && x.CorrelationId == deliveryId.ToString()).SingleAsync(Ct);
            attempt.AdapterName.ShouldBe(WebhookConstants.AdapterName);
            attempt.Operation.ShouldBe("order.created");   // operation = event type
            attempt.GroupName.ShouldBe("loopback-endpoint");   // group = endpoint
            attempt.CorrelationId.ShouldBe(deliveryId.ToString());
            attempt.Outcome.ShouldBe(AdapterCallOutcome.Success);
            attempt.StatusCode.ShouldBe(200);
            attempt.Attempts.ShouldBe(1);
            attempt.ResponseBody.ShouldBe(HookResponseBody);   // webhooks capture response bodies always
            attempt.RequestBody.ShouldBeNull();   // ...but never request bodies (payload already on the row)
            attempt.RequestHeaders.ShouldBeNull();   // warp-webhooks adapter leaves CaptureHeaders = None
            attempt.ResponseHeaders.ShouldBeNull();
            attempt.RequestSummary.ShouldBe("POST http://localhost/hook");
            attempt.ExceptionType.ShouldBeNull();
            attempt.ExceptionMessage.ShouldBeNull();
            attempt.MachineName.ShouldBe(Environment.MachineName);
            attempt.Id.ShouldNotBe(Guid.Empty);
            attempt.Timestamp.ShouldBeInRange(startedAt, now);
            attempt.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
            attempt.TraceId.ShouldBeNull();
            attempt.TagsJson.ShouldBeNull();
            attempt.ExpireAt.ShouldNotBeNull();   // Timestamp + WebhookDeliveryRetention (aligned)
            attempt.ExpireAt!.Value.ShouldBeGreaterThan(attempt.Timestamp);
        }

        // ============================ 4. WebhookDelivery row ============================
        await using (var ctx = _fixture.CreateContext())
        {
            var d = await ctx.Set<WebhookDelivery>().AsNoTracking().SingleAsync(x => x.Id == deliveryId, Ct);
            d.EventType.ShouldBe("order.created");
            d.EventId.ShouldBe("evt-fullstack-1");
            d.Url.ShouldBe("http://localhost/hook");
            d.GroupName.ShouldBe("loopback-endpoint");
            d.Reference.ShouldBe("ref-fullstack-1");
            d.PayloadJson.ShouldBe("{\"hello\":\"world\"}");
            d.SigningMode.ShouldBe(WebhookSigning.None);
            d.Secret.ShouldBeNull();
            d.HeadersJson.ShouldBeNull();
            d.SuccessCodesJson.ShouldBeNull();
            d.RetrySchedule.ShouldBeEmpty();
            d.Status.ShouldBe(WebhookDeliveryStatus.Delivered);
            d.AttemptCount.ShouldBe(1);
            d.ExhaustedCallbackPending.ShouldBeFalse();
            d.NextAttemptAt.ShouldBeNull();   // nulled on Delivered
            d.CreatedAt.ShouldBeInRange(startedAt, now);
            d.ExpireAt.ShouldNotBeNull();
            d.ExpireAt!.Value.ShouldBeGreaterThan(d.CreatedAt);
        }

        // ================= 5. EndpointCallLog — the direct /inbound request =================
        await WaitUntil(async ctx => await ctx.Set<EndpointCallLog>().AnyAsync(x => x.RouteTemplate == "/inbound", Ct));
        await using (var ctx = _fixture.CreateContext())
        {
            var ep = await ctx.Set<EndpointCallLog>().AsNoTracking().Where(x => x.RouteTemplate == "/inbound").SingleAsync(Ct);
            ep.Id.ShouldNotBe(Guid.Empty);
            ep.Method.ShouldBe("POST");
            ep.RouteTemplate.ShouldBe("/inbound");
            ep.Operation.ShouldNotBeNullOrEmpty();
            ep.GroupName.ShouldBe("acme-portal");   // from GroupSelector reading X-Client
            ep.Outcome.ShouldBe(AdapterCallOutcome.Success);
            ep.StatusCode.ShouldBe(200);
            ep.RemoteIp.ShouldBe("198.51.100.9");   // from X-Forwarded-For (UseForwardedForIp)
            ep.UserAgent.ShouldBe("FullStackTest/1.0");
            ep.User.ShouldBeNull();   // no auth
            ep.RequestBody.ShouldBe("{\"src\":\"inbound\"}");
            ep.ResponseBody.ShouldBe(InboundResponseBody);
            ep.RequestHeaders.ShouldNotBeNullOrEmpty();
            ep.ResponseHeaders.ShouldNotBeNullOrEmpty();
            ep.ExceptionType.ShouldBeNull();
            ep.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
            ep.MachineName.ShouldBe(Environment.MachineName);
            ep.Timestamp.ShouldBeInRange(startedAt, now);
            ep.ExceptionMessage.ShouldBeNull();
            ep.TraceId.ShouldNotBeNull();   // inbound runs under the ASP.NET request Activity → trace id captured
            ep.TraceId!.Value.ShouldNotBe(Guid.Empty);
            ep.TagsJson.ShouldBeNull();
            ep.ExpireAt.ShouldNotBeNull();
            ep.ExpireAt!.Value.ShouldBeGreaterThan(ep.Timestamp);   // Timestamp + 7d global retention

            // The looped-back adapter (/vendor) and webhook (/hook) calls are also inbound Warp endpoints.
            await AssertLoopedBackEndpointAsync(ctx, "/vendor", "{\"src\":\"vendor\"}", VendorResponseBody, startedAt, now);
            await AssertLoopedBackEndpointAsync(ctx, "/hook", "{\"hello\":\"world\"}", HookResponseBody, startedAt, now);
        }

        // ============================ 6. Dashboard API (the UI's JSON) ============================
        var addons = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", Ct);
        addons.ShouldNotBeNull();
        addons.Adapters.ShouldBeTrue();
        addons.Endpoints.ShouldBeTrue();
        addons.Webhooks.ShouldBeTrue();

        // Adapters: list item + detail + call detail for the vendor adapter.
        var adapterList = await client.GetFromJsonAsync<List<AdapterListItemModel>>("/warp/api/adapters", Ct);
        var vendor = adapterList!.Single(x => string.Equals(x.Name, VendorAdapter, StringComparison.Ordinal));
        vendor.TotalCalls.ShouldBe(1);
        vendor.ErrorCount.ShouldBe(0);
        vendor.ErrorRate.ShouldBe(0);
        vendor.AvgDurationMs.ShouldBeGreaterThanOrEqualTo(0);

        var vendorDetail = await client.GetFromJsonAsync<AdapterDetailModel>($"/warp/api/adapters/{VendorAdapter}", Ct);
        vendorDetail!.Name.ShouldBe(VendorAdapter);
        vendorDetail.TotalCalls.ShouldBe(1);
        vendorDetail.ErrorCount.ShouldBe(0);
        vendorDetail.Operations.ShouldContain(o => string.Equals(o.Operation, "VendorPing", StringComparison.Ordinal) && o.Calls == 1);
        var recentVendor = vendorDetail.RecentCalls.Single(c => string.Equals(c.Operation, "VendorPing", StringComparison.Ordinal));
        recentVendor.Outcome.ShouldBe(AdapterCallOutcome.Success);
        recentVendor.StatusCode.ShouldBe(200);
        recentVendor.GroupName.ShouldBe("vendor-grp");
        recentVendor.CorrelationId.ShouldBe("vendor-corr");

        var vendorCall = await client.GetFromJsonAsync<AdapterCallDetailModel>($"/warp/api/adapters/{VendorAdapter}/calls/{recentVendor.Id}", Ct);
        vendorCall!.Operation.ShouldBe("VendorPing");
        vendorCall.RequestBody.ShouldBe("{\"src\":\"vendor\"}");
        vendorCall.ResponseBody.ShouldBe(VendorResponseBody);
        vendorCall.StatusCode.ShouldBe(200);

        // Endpoints: list item + detail for /inbound.
        var endpointList = await client.GetFromJsonAsync<List<EndpointListItemModel>>("/warp/api/endpoints", Ct);
        var inboundItem = endpointList!.Single(x => string.Equals(x.RouteTemplate, "/inbound", StringComparison.Ordinal));
        inboundItem.Method.ShouldBe("POST");
        inboundItem.TotalCalls.ShouldBe(1);
        inboundItem.ErrorCount.ShouldBe(0);

        var inboundDetail = await client.GetFromJsonAsync<EndpointDetailModel>($"/warp/api/endpoints/{inboundItem.Id}", Ct);
        inboundDetail!.RouteTemplate.ShouldBe("/inbound");
        inboundDetail.Method.ShouldBe("POST");
        inboundDetail.TotalCalls.ShouldBe(1);
        var recentInbound = inboundDetail.RecentCalls.ShouldHaveSingleItem();
        recentInbound.Outcome.ShouldBe(AdapterCallOutcome.Success);
        recentInbound.StatusCode.ShouldBe(200);
        recentInbound.RemoteIp.ShouldBe("198.51.100.9");
        recentInbound.UserAgent.ShouldBe("FullStackTest/1.0");
        recentInbound.GroupName.ShouldBe("acme-portal");

        // Webhooks: list item + detail (secret redacted, attempt recorded).
        var webhookList = await client.GetFromJsonAsync<PagedList<WebhookDeliveryListItem>>("/warp/api/webhooks", Ct);
        var wItem = webhookList!.Items.Single(x => x.Id == deliveryId);
        wItem.EventType.ShouldBe("order.created");
        wItem.Status.ShouldBe(WebhookDeliveryStatus.Delivered);
        wItem.AttemptCount.ShouldBe(1);
        wItem.GroupName.ShouldBe("loopback-endpoint");

        var wDetail = await client.GetFromJsonAsync<WebhookDeliveryDetail>($"/warp/api/webhooks/{deliveryId}", Ct);
        wDetail!.Id.ShouldBe(deliveryId);
        wDetail.EventId.ShouldBe("evt-fullstack-1");
        wDetail.Url.ShouldBe("http://localhost/hook");
        wDetail.Reference.ShouldBe("ref-fullstack-1");
        wDetail.PayloadJson.ShouldBe("{\"hello\":\"world\"}");
        wDetail.SigningMode.ShouldBe(WebhookSigning.None);
        wDetail.HasSecret.ShouldBeFalse();
        wDetail.Status.ShouldBe(WebhookDeliveryStatus.Delivered);
        wDetail.AttemptCount.ShouldBe(1);
        var attemptItem = wDetail.Attempts.ShouldHaveSingleItem();
        attemptItem.Outcome.ShouldBe(AdapterCallOutcome.Success);
        attemptItem.StatusCode.ShouldBe(200);

        // ==================== 7. Counter rows (the stats pipeline behind the dashboard) ====================
        await using (var ctx = _fixture.CreateContext())
        {
            (await ctx.Set<Counter>().AnyAsync(x => x.Key.Contains(VendorAdapter), Ct)).ShouldBeTrue();
            (await ctx.Set<Counter>().AnyAsync(x => x.Key.Contains(WebhookConstants.AdapterName), Ct)).ShouldBeTrue();
            (await ctx.Set<Counter>().AnyAsync(x => x.Key.Contains("/inbound"), Ct)).ShouldBeTrue();
        }

        // ==================== 8. AdapterDefinition rows (registration ledger) ====================
        await using (var ctx = _fixture.CreateContext())
        {
            var vendorDef = await ctx.Set<AdapterDefinition>().AsNoTracking().SingleAsync(x => x.Name == VendorAdapter, Ct);
            vendorDef.Id.ShouldNotBe(Guid.Empty);
            vendorDef.Name.ShouldBe(VendorAdapter);
            vendorDef.ConfigSummary.ShouldNotBeNullOrEmpty();
            vendorDef.GroupLabel.ShouldBe("Group");   // vendor set no GroupLabel → default
            vendorDef.CallLogRetentionCount.ShouldBeNull();
            vendorDef.SharedPolicyJson.ShouldBeNull();
            vendorDef.SharedPolicyHash.ShouldBeNull();
            vendorDef.HasPolicyConflict.ShouldBeFalse();
            vendorDef.FirstSeenAt.ShouldBeInRange(startedAt, now);
            vendorDef.LastSeenAt.ShouldBeInRange(startedAt, now);

            var hookDef = await ctx.Set<AdapterDefinition>().AsNoTracking().SingleAsync(x => x.Name == WebhookConstants.AdapterName, Ct);
            hookDef.Name.ShouldBe(WebhookConstants.AdapterName);
            hookDef.GroupLabel.ShouldBe("Endpoint");   // set by the webhook adapter registration
            hookDef.HasPolicyConflict.ShouldBeFalse();
            hookDef.SharedPolicyJson.ShouldBeNull();
            hookDef.FirstSeenAt.ShouldBeInRange(startedAt, now);
            hookDef.LastSeenAt.ShouldBeInRange(startedAt, now);
        }

        await app.StopAsync(Ct);
    }

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

    // Full field assertion for a looped-back inbound endpoint row (the adapter /vendor and webhook /hook
    // calls). These callers send no X-Client / X-Forwarded-For / User-Agent, so group/IP/UA are null.
    private static async Task AssertLoopedBackEndpointAsync(TestContext ctx, string route, string requestBody, string responseBody, DateTime startedAt, DateTime now)
    {
        var ep = await ctx.Set<EndpointCallLog>().AsNoTracking().Where(x => x.RouteTemplate == route).SingleAsync(Ct);
        ep.Id.ShouldNotBe(Guid.Empty);
        ep.Method.ShouldBe("POST");
        ep.RouteTemplate.ShouldBe(route);
        ep.Operation.ShouldNotBeNullOrEmpty();
        ep.GroupName.ShouldBeNull();
        ep.Outcome.ShouldBe(AdapterCallOutcome.Success);
        ep.StatusCode.ShouldBe(200);
        ep.RemoteIp.ShouldBeNull();
        ep.UserAgent.ShouldBeNull();
        ep.User.ShouldBeNull();
        ep.RequestBody.ShouldBe(requestBody);
        ep.ResponseBody.ShouldBe(responseBody);
        ep.RequestHeaders.ShouldNotBeNullOrEmpty();
        ep.ResponseHeaders.ShouldNotBeNullOrEmpty();
        ep.ExceptionType.ShouldBeNull();
        ep.ExceptionMessage.ShouldBeNull();
        ep.MachineName.ShouldBe(Environment.MachineName);
        ep.Timestamp.ShouldBeInRange(startedAt, now);
        ep.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        ep.TraceId.ShouldNotBeNull();   // inbound runs under the ASP.NET request Activity → trace id captured
        ep.TraceId!.Value.ShouldNotBe(Guid.Empty);
        ep.TagsJson.ShouldBeNull();
        ep.ExpireAt.ShouldNotBeNull();
        ep.ExpireAt!.Value.ShouldBeGreaterThan(ep.Timestamp);
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

            // One worker is all this test needs; the default (up to 20) would hammer the shared container and
            // destabilize timing-sensitive neighbours (good-neighbour footprint).
            opt.WorkerCount = 1;
            opt.PollingInterval = TimeSpan.FromMilliseconds(100);
            opt.MaxPollingInterval = TimeSpan.FromMilliseconds(100);
            opt.PollingIntervalFactor = 1.0;
            opt.ScheduledActivationInterval = TimeSpan.FromMilliseconds(250);

            opt.AddAdapters();
            opt.AddEndpointObservability(o =>
            {
                o.CaptureRequestBodies = CaptureMode.Always;
                o.CaptureResponseBodies = CaptureMode.Always;
                o.CaptureHeaders = CaptureMode.Always;
                o.UseForwardedForIp = true;
                o.GroupSelector = ctx => ctx.Request.Headers["X-Client"].FirstOrDefault();
            });
            opt.AddAdapter(VendorAdapter, a =>
            {
                a.BaseUrl = new Uri("http://localhost/");
                a.Recording.CaptureRequestBodies = CaptureMode.Always;
                a.Recording.CaptureResponseBodies = CaptureMode.Always;
                a.Recording.CaptureHeaders = CaptureMode.Always;
            });
        });

        // Loopback: route the vendor adapter client and the warp-webhooks client back into this app's own
        // in-memory TestServer, resolved lazily (after the app has started, so the TestServer exists).
        builder.Services.AddHttpClient(VendorAdapter)
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

/// <summary>The direct-inbound loopback endpoint. Returns a known body so response capture is assertable.</summary>
public sealed record InboundRequest : IRequest<IResult>;

[WarpHttpPost("/inbound")]
public sealed class InboundHttpHandler : IRequestHandler<InboundRequest, IResult>
{
    public Task<IResult> HandleAsync(InboundRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Results.Text("inbound-response-payload"));
}

/// <summary>The outbound-adapter target endpoint.</summary>
public sealed record VendorRequest : IRequest<IResult>;

[WarpHttpPost("/vendor")]
public sealed class VendorHttpHandler : IRequestHandler<VendorRequest, IResult>
{
    public Task<IResult> HandleAsync(VendorRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Results.Text("vendor-response-payload"));
}

/// <summary>The webhook-delivery target endpoint.</summary>
public sealed record HookRequest : IRequest<IResult>;

[WarpHttpPost("/hook")]
public sealed class HookHttpHandler : IRequestHandler<HookRequest, IResult>
{
    public Task<IResult> HandleAsync(HookRequest request, CancellationToken cancellationToken)
        => Task.FromResult(Results.Text("hook-response-payload"));
}

/// <summary>A job whose handler logs (→ JobLog) and makes one fully-tagged outbound adapter call (→ AdapterCallLog).</summary>
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
        _logger.LogInformation("full-stack job pinged the vendor adapter");

        var client = _httpClientFactory.CreateClient("loopback-vendor");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/vendor")
        {
            Content = new StringContent("{\"src\":\"vendor\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.WithWarpOperation("VendorPing");
        request.WithWarpGroup("vendor-grp");
        request.WithWarpCorrelation("vendor-corr");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
