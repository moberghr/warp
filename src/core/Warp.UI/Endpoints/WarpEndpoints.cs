using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.BackgroundServices;
using Warp.Core.ClientObservability;
using Warp.Core.Concurrency;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.RateLimit;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.Core.Webhooks;
using Warp.UI.DashboardPush;
using Warp.UI.Extensions;
using Warp.UI.UIMiddleware;

namespace Warp.UI.Endpoints;

public static class WarpEndpoints
{
    public static void MapWarpApiEndpoints(this WebApplication app, WarpUIOptions options, List<IWarpUIExtension> extensions)
    {
        var apiGroup = app.MapGroup($"{options.RoutePrefix}/api");

        if (options.Authorization != null)
        {
            var filter = options.Authorization;
            apiGroup.AddEndpointFilter(async (context, next) =>
            {
                if (!filter.Authorize(context.HttpContext))
                {
                    return Results.Unauthorized();
                }

                return await next(context);
            });
        }

        apiGroup.MapGet("status", async ([FromServices] IDashboardStatsService statsService) => await statsService.GetWarpStatus());

        apiGroup.MapGet("jobs/enqueued", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string? application) => await jobQueryService.GetJobsList(request, State.Enqueued, application));

        apiGroup.MapGet("jobs/completed", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string? application) => await jobQueryService.GetJobsList(request, State.Completed, application));

        apiGroup.MapGet("jobs/failed", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string? application) => await jobQueryService.GetJobsList(request, State.Failed, application));

        apiGroup.MapGet("jobs/failed/types", async ([FromServices] IJobQueryService jobQueryService) => await jobQueryService.GetFailedJobTypeCounts());

        apiGroup.MapGet("jobs/failed/by-type", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string type) => await jobQueryService.GetFailedJobsByType(request, type));

        apiGroup.MapGet("jobs/by-type", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string type, [FromQuery] string? state, [FromQuery] string? application) =>
            await jobQueryService.GetJobsByType(request, type, Enum.TryParse<State>(state, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null, application));

        apiGroup.MapPost("jobs/failed/delete-by-type", async ([FromServices] IJobCommandService jobCommandService, [FromQuery] string type) => await jobCommandService.DeleteFailedJobsByType(type));

        apiGroup.MapPost("jobs/failed/requeue-by-type", async ([FromServices] IJobCommandService jobCommandService, [FromQuery] string type) => await jobCommandService.RequeueFailedJobsByType(type));

        apiGroup.MapGet("jobs/processing", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetJobStatesInProcess(request));

        apiGroup.MapGet("jobs/scheduled", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetScheduledJobs(request));

        apiGroup.MapGet("jobs/awaiting", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetAwaitingJobs(request));

        apiGroup.MapGet("jobs/deleted", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request, [FromQuery] string? application) => await jobQueryService.GetJobsList(request, State.Deleted, application));

        apiGroup.MapGet("jobs/{jobId}/siblings", async ([FromServices] IJobQueryService jobQueryService, Guid jobId, [AsParameters] BaseListRequest request) => await jobQueryService.GetSiblingJobs(jobId, request));

        apiGroup.MapGet("jobs/{jobId}/children", async ([FromServices] IJobQueryService jobQueryService, Guid jobId, [AsParameters] BaseListRequest request) => await jobQueryService.GetChildJobs(jobId, request));

        apiGroup.MapGet("jobs/{jobId}/trace", async ([FromServices] IJobQueryService jobQueryService, Guid jobId, [AsParameters] BaseListRequest request) => await jobQueryService.GetTraceJobs(jobId, request));

        apiGroup.MapGet("trace/{traceId}", async ([FromServices] IJobQueryService jobQueryService, Guid traceId) => await jobQueryService.GetTraceTree(traceId));

        // Unified trace view (§8.28): everything for a trace id — client request + endpoint call + jobs +
        // outbound adapter calls — unioned from existing rows. Superset of the job-only GetTraceTree above.
        apiGroup.MapGet("traces/{traceId}", async ([FromServices] ITraceQueryService svc, Guid traceId, CancellationToken ct) =>
        {
            var trace = await svc.GetTrace(traceId, ct);

            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        apiGroup.MapGet("detail/{id}", async ([FromServices] IJobQueryService jobQueryService, Guid id) =>
        {
            var result = await jobQueryService.GetJobDetailById(id);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        apiGroup.MapPost("jobs/{jobId}/requeue", async ([FromServices] IJobCommandService jobCommandService, Guid jobId) => await jobCommandService.RequeueJob(jobId));

        apiGroup.MapPost("jobs/{jobId}/delete", async ([FromServices] IJobCommandService jobCommandService, Guid jobId) => await jobCommandService.DeleteJob(jobId));

        apiGroup.MapPost("jobs/bulk/delete", async ([FromServices] IJobCommandService jobCommandService, [FromBody] BulkJobRequest request) => await jobCommandService.BulkDeleteJobs(request.JobIds));

        apiGroup.MapPost("jobs/bulk/requeue", async ([FromServices] IJobCommandService jobCommandService, [FromBody] BulkJobRequest request) => await jobCommandService.BulkRequeueJobs(request.JobIds));

        apiGroup.MapGet("messages", async ([FromServices] IJobGroupQueryService svc, [AsParameters] BaseListRequest request, string? state) => await svc.GetJobGroups(JobKind.Message, request, state));

        apiGroup.MapGet("messages/{messageId}", async ([FromServices] IJobGroupQueryService svc, Guid messageId) =>
        {
            var model = await svc.GetJobGroupById(messageId);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        apiGroup.MapGet("messages/{messageId}/jobs", async ([FromServices] IJobGroupQueryService svc, Guid messageId, [AsParameters] BaseListRequest request, string? state) => await svc.GetJobGroupJobs(messageId, request, state));

        apiGroup.MapGet("messages/{messageId}/jobs/counts", async ([FromServices] IJobGroupQueryService svc, Guid messageId) => await svc.GetJobGroupJobCounts(messageId));

        apiGroup.MapGet("recurring", async ([FromServices] IRecurringJobService recurringJobService, [AsParameters] BaseListRequest request) => await recurringJobService.GetRecurringJobs(request));

        apiGroup.MapGet("recurring/{id}", async ([FromServices] IRecurringJobService recurringJobService, int id) =>
        {
            var model = await recurringJobService.GetRecurringJobById(id);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        apiGroup.MapGet("recurring/{id}/jobs", async ([FromServices] IRecurringJobService recurringJobService, int id, [AsParameters] BaseListRequest request) => await recurringJobService.GetRecurringJobHistory(id, request));

        apiGroup.MapPost("recurring/{id}/trigger", async ([FromServices] IRecurringJobService recurringJobService, int id) => await recurringJobService.TriggerRecurringJob(id));

        apiGroup.MapPost("recurring/{id}/enable", async ([FromServices] IRecurringJobService recurringJobService, int id) => await recurringJobService.EnableRecurringJob(id));

        apiGroup.MapPost("recurring/{id}/disable", async ([FromServices] IRecurringJobService recurringJobService, int id) => await recurringJobService.DisableRecurringJob(id));

        apiGroup.MapDelete("recurring/{id}", async ([FromServices] IRecurringJobService recurringJobService, int id) => await recurringJobService.DeleteRecurringJob(id));

        apiGroup.MapGet("batches", async ([FromServices] IJobGroupQueryService svc, [AsParameters] BaseListRequest request, string? state) => await svc.GetJobGroups(JobKind.Batch, request, state));

        apiGroup.MapGet("batches/{batchId}", async ([FromServices] IJobGroupQueryService svc, Guid batchId) =>
        {
            var model = await svc.GetJobGroupById(batchId);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        apiGroup.MapGet("batches/{batchId}/jobs", async ([FromServices] IJobGroupQueryService svc, Guid batchId, [AsParameters] BaseListRequest request, string? state) => await svc.GetJobGroupJobs(batchId, request, state));

        apiGroup.MapGet("batches/{batchId}/jobs/counts", async ([FromServices] IJobGroupQueryService svc, Guid batchId) => await svc.GetJobGroupJobCounts(batchId));

        apiGroup.MapPost("batches/{batchId}/cancel", async ([FromServices] IJobCommandService jobCommandService, Guid batchId) => await jobCommandService.CancelBatch(batchId));

        apiGroup.MapGet("stats/history", async ([FromServices] IDashboardStatsService statsService, [FromQuery] int? hours) => await statsService.GetStatsHistory(hours ?? 24));

        apiGroup.MapGet("stats/counters", async ([FromServices] IDashboardStatsService statsService) => await statsService.GetCounters());

        apiGroup.MapGet("stats/counters/history", async ([FromServices] IDashboardStatsService statsService, [FromQuery] int? hours) => await statsService.GetCountersHistory(hours ?? 24));

        // Single discovery endpoint. The dashboard probes opt-in addons in one round-trip
        // rather than firing a GET against each addon's data route and treating 404 as the
        // signal. Always returns 200; per-addon flags reflect DI service presence. This
        // replaced the per-addon hide-on-404 probes (e.g. dashboard/push/probe) that were
        // removed when the dashboard switched to single-call discovery.
        apiGroup.MapGet("addons", (
            [FromServices] IConcurrencyLimitManager? concurrency,
            [FromServices] IRateLimitManager? rateLimits,
            [FromServices] IDashboardPushMarker? push,
            [FromServices] ISagaQueryService? sagas,
            [FromServices] IAdapterRecordingMarker? adapters,
            [FromServices] IEndpointObservabilityMarker? endpoints,
            [FromServices] IClientObservabilityMarker? client,
            [FromServices] IWebhookRedeliveryEnqueuer? webhooks,
            [FromServices] Warp.Core.Slo.ISloMarker? slo,
            [FromServices] IOptions<WarpConfiguration> configuration) =>
            Results.Ok(new WarpAddonsInfo
            {
                Concurrency = concurrency is not null,
                Push = push is not null,
                RateLimits = rateLimits is not null,
                Sagas = sagas is not null,

                // IWarpAdapters is registered only by AddAdapters(); IAdapterQueryService (always
                // registered by AddWarp for dashboard-only processes) can't gate the flag.
                Adapters = adapters is not null,

                // IEndpointObservabilityMarker is registered only by AddEndpointObservability() (regardless of
                // sink); IEndpointQueryService (always registered by AddWarp for dashboard-only processes)
                // can't gate the flag. The endpoints nav shows wherever inbound requests are being observed.
                Endpoints = endpoints is not null,

                // IClientObservabilityMarker is registered only by AddClientObservability() (regardless of
                // sink); IClientEventQueryService (always registered by AddWarp) can't gate the flag.
                Client = client is not null,

                // IWebhookRedeliveryEnqueuer is registered only by AddWebhooks(); IWebhookQueryService /
                // IWebhookCommandService (always registered by AddWarp for dashboard-only processes) can't
                // gate the flag. The webhooks nav shows only where a delivery can actually be executed.
                Webhooks = webhooks is not null,

                // ISloMarker is registered only by AddSlo(); the SLO query/command services are always
                // registered by AddWarp, so this gates the nav, not the API.
                Slo = slo is not null,

                // Multi-app observability (§8.19). Unlike the other flags (which gate on a DI service), this
                // reads config: the feature is on when this process set an ApplicationName. The Applications
                // page itself (the renamed Servers page) is always available; the flag toggles app-grouping.
                Applications = configuration.Value.ApplicationName is not null,
            }));

        apiGroup.MapGet("concurrency", async ([FromServices] IConcurrencyLimitManager? mgr, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            var list = await mgr.ListLimits(ct);

            return Results.Ok(list);
        });

        apiGroup.MapGet("concurrency/{name}", async ([FromServices] IConcurrencyLimitManager? mgr, string name, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var info = await mgr.GetLimit(name, ct);

            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        apiGroup.MapPost("concurrency", async ([FromServices] IConcurrencyLimitManager? mgr, [FromBody] UpsertConcurrencyLimitRequest body, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Name) || body.Limit < 1)
            {
                return Results.BadRequest();
            }

            await mgr.AddOrUpdateLimit(body.Name, body.Limit, ct);
            var info = await mgr.GetLimit(body.Name, ct);

            return Results.Ok(info);
        });

        apiGroup.MapPut("concurrency/{name}", async ([FromServices] IConcurrencyLimitManager? mgr, string name, [FromBody] UpdateConcurrencyLimitRequest body, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name) || body is null || body.Limit < 1)
            {
                return Results.BadRequest();
            }

            await mgr.AddOrUpdateLimit(name, body.Limit, ct);
            var info = await mgr.GetLimit(name, ct);

            return Results.Ok(info);
        });

        apiGroup.MapDelete("concurrency/{name}", async ([FromServices] IConcurrencyLimitManager? mgr, string name, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var removed = await mgr.RemoveLimit(name, ct);

            return removed ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapGet("ratelimits", async ([FromServices] IRateLimitManager? mgr, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            var list = await mgr.ListLimits(ct);

            return Results.Ok(list);
        });

        apiGroup.MapGet("ratelimits/{name}", async ([FromServices] IRateLimitManager? mgr, string name, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var info = await mgr.GetLimit(name, ct);

            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        apiGroup.MapPost("ratelimits", async ([FromServices] IRateLimitManager? mgr, [FromBody] UpsertRateLimitRequest body, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            // Validate at the endpoint so a too-long name returns 400 rather than bubbling
            // ArgumentException from the manager as a 500. Length cap matches MaxNameLength
            // in RateLimitManager.
            if (body is null
                || string.IsNullOrWhiteSpace(body.Name)
                || body.Name.Length > 200
                || body.Count < 1
                || body.WindowSeconds < 1)
            {
                return Results.BadRequest();
            }

            await mgr.AddOrUpdateLimit(body.Name, body.Count, body.WindowSeconds, ct);
            var info = await mgr.GetLimit(body.Name, ct);

            return Results.Ok(info);
        });

        apiGroup.MapPut("ratelimits/{name}", async ([FromServices] IRateLimitManager? mgr, string name, [FromBody] UpdateRateLimitRequest body, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name)
                || name.Length > 200
                || body is null
                || body.Count < 1
                || body.WindowSeconds < 1)
            {
                return Results.BadRequest();
            }

            await mgr.AddOrUpdateLimit(name, body.Count, body.WindowSeconds, ct);
            var info = await mgr.GetLimit(name, ct);

            return Results.Ok(info);
        });

        apiGroup.MapDelete("ratelimits/{name}", async ([FromServices] IRateLimitManager? mgr, string name, CancellationToken ct) =>
        {
            if (mgr is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var removed = await mgr.RemoveLimit(name, ct);

            return removed ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapGet("servers", async ([FromServices] IDashboardStatsService statsService) => await statsService.GetServers());

        apiGroup.MapGet("servers/{serverId}", async ([FromServices] IDashboardStatsService statsService, Guid serverId) =>
        {
            var model = await statsService.GetServerById(serverId);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        apiGroup.MapGet("servers/{serverId}/tasks", async ([FromServices] IDashboardStatsService statsService, Guid serverId) => await statsService.GetServerTaskSummaries(serverId));

        apiGroup.MapGet("servers/{serverId}/logs", async ([FromServices] IDashboardStatsService statsService, Guid serverId, [AsParameters] BaseListRequest request, [FromQuery] string? taskName) => await statsService.GetServerLogs(serverId, request, taskName));

        apiGroup.MapPost("servers/{serverId}/pause", async ([FromServices] IServerCommandService svc, Guid serverId) =>
        {
            var result = await svc.PauseServer(serverId);
            return result ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapPost("servers/{serverId}/resume", async ([FromServices] IServerCommandService svc, Guid serverId) =>
        {
            var result = await svc.ResumeServer(serverId);
            return result ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapPost("groups/{groupId}/pause", async ([FromServices] IServerCommandService svc, Guid groupId) =>
        {
            var result = await svc.PauseWorkerGroup(groupId);
            return result ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapPost("groups/{groupId}/resume", async ([FromServices] IServerCommandService svc, Guid groupId) =>
        {
            var result = await svc.ResumeWorkerGroup(groupId);
            return result ? Results.Ok() : Results.NotFound();
        });

        apiGroup.MapGet("workers/{workerId}", async ([FromServices] IDashboardStatsService statsService, Guid workerId) =>
        {
            var model = await statsService.GetWorkerById(workerId);
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        apiGroup.MapGet("workers/{workerId}/logs", async ([FromServices] IDashboardStatsService statsService, Guid workerId, [AsParameters] BaseListRequest request) => await statsService.GetWorkerJobLogs(workerId, request));

        apiGroup.MapGet("created", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetJobsList(request, State.Enqueued));

        apiGroup.MapGet("completed", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetJobsList(request, State.Completed));

        apiGroup.MapGet("failed", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetJobsList(request, State.Failed));

        apiGroup.MapGet("processing", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetJobStatesInProcess(request));

        apiGroup.MapGet("scheduled", async ([FromServices] IJobQueryService jobQueryService, [AsParameters] BaseListRequest request) => await jobQueryService.GetScheduledJobs(request));

        // Sagas — endpoints return 404 when the addon isn't registered (drives sidebar hide).
        apiGroup.MapGet("sagas", async (
            [FromServices] ISagaQueryService? svc,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] string? type,
            [FromQuery] string? key) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var request = new BaseListRequest { Page = page, PageSize = pageSize > 0 ? pageSize : 20 };
            var result = await svc.GetSagas(request, type, key);

            return Results.Ok(result);
        });

        apiGroup.MapGet("sagas/types", async ([FromServices] ISagaQueryService? svc) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var types = await svc.GetSagaTypes();

            return Results.Ok(types);
        });

        apiGroup.MapGet("sagas/stats", async ([FromServices] ISagaQueryService? svc) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var stats = await svc.GetStats();

            return Results.Ok(stats);
        });

        apiGroup.MapGet("sagas/{id}", async ([FromServices] ISagaQueryService? svc, Guid id) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var saga = await svc.GetSagaById(id);

            return saga is null ? Results.NotFound() : Results.Ok(saga);
        });

        apiGroup.MapGet("sagas/{id}/activity", async ([FromServices] ISagaQueryService? svc, Guid id) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var activity = await svc.GetSagaActivity(id);

            return Results.Ok(activity);
        });

        apiGroup.MapDelete("sagas/{id}", async ([FromServices] ISagaCommandService? svc, Guid id) =>
        {
            if (svc is null)
            {
                return Results.NotFound();
            }

            var removed = await svc.ForceComplete(id);

            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Adapters — outbound service-call observability. IAdapterQueryService is always registered by
        // AddWarp (dashboard-only processes resolve it), so these endpoints are non-nullable; the
        // sidebar nav is gated on the addons flag (IWarpAdapters presence), not on a 404.
        apiGroup.MapGet("adapters", async ([FromServices] IAdapterQueryService svc, [FromQuery] string? application, CancellationToken ct) =>
            string.IsNullOrEmpty(application)
                ? Results.Ok(await svc.GetAdapters(ct))
                : Results.Ok(await svc.GetAdapterStatsByApplication(application, ct)));

        apiGroup.MapGet("adapters/history", async ([FromServices] IAdapterQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetGlobalHistory(ct)));

        apiGroup.MapGet("adapters/{name}", async ([FromServices] IAdapterQueryService svc, string name, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var detail = await svc.GetAdapterDetail(name, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapGet("adapters/{name}/calls/{id}", async ([FromServices] IAdapterQueryService svc, string name, Guid id, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest();
            }

            var detail = await svc.GetCallDetail(name, id, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // Endpoints — inbound endpoint observability. IEndpointQueryService is always registered by AddWarp
        // (dashboard-only processes resolve it); the sidebar nav is gated on the addons flag
        // (IEndpointCallRecorder presence), not on a 404. The {id} is the URL-safe encoded route identity.
        apiGroup.MapGet("endpoints", async ([FromServices] IEndpointQueryService svc, [FromQuery] string? application, CancellationToken ct) =>
            string.IsNullOrEmpty(application)
                ? Results.Ok(await svc.GetEndpoints(ct))
                : Results.Ok(await svc.GetEndpointStatsByApplication(application, ct)));

        apiGroup.MapGet("endpoints/history", async ([FromServices] IEndpointQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetGlobalHistory(ct)));

        apiGroup.MapGet("endpoints/{id}", async ([FromServices] IEndpointQueryService svc, string id, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest();
            }

            var detail = await svc.GetEndpointDetail(id, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapGet("endpoints/{id}/calls/{callId}", async ([FromServices] IEndpointQueryService svc, string id, Guid callId, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest();
            }

            var detail = await svc.GetCallDetail(id, callId, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // Client (browser) observability (§8.27). IClientEventQueryService is always registered by AddWarp, so
        // these resolve in dashboard-only / publisher-only processes without the ingest endpoint.
        apiGroup.MapGet("client/summary", async ([FromServices] IClientEventQueryService svc, [FromQuery] string? application, CancellationToken ct) =>
            Results.Ok(await svc.GetSummary(application, ct)));

        apiGroup.MapGet("client/applications", async ([FromServices] IClientEventQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetApplications(ct)));

        apiGroup.MapGet("client/events", async (
            [FromServices] IClientEventQueryService svc,
            [FromQuery] string? application,
            [FromQuery] string? type,
            [FromQuery] string? session,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            // page/pageSize are NULLABLE so the SPA can omit them for the first page — a non-nullable int
            // query param would 400 when absent (matches the webhooks route pattern).
            var parsedType = Enum.TryParse<ClientEventType>(type, ignoreCase: true, out var t) ? t : (ClientEventType?)null;
            var filter = new ClientEventFilter
            {
                Application = application,
                Type = parsedType,
                SessionId = session,
                Page = page ?? 0,
                PageSize = pageSize ?? 50,
            };

            return Results.Ok(await svc.GetEvents(filter, ct));
        });

        apiGroup.MapGet("client/events/{id}", async ([FromServices] IClientEventQueryService svc, Guid id, CancellationToken ct) =>
        {
            var detail = await svc.GetEvent(id, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapGet("client/sessions/{sessionId}", async ([FromServices] IClientEventQueryService svc, string sessionId, CancellationToken ct) =>
        {
            var session = await svc.GetSession(sessionId, ct);

            return session is null ? Results.NotFound() : Results.Ok(session);
        });

        // Error grouping / Issues per §8.29. The query + command services are always registered by AddWarp, so
        // dashboard-only processes resolve them and these data routes are non-nullable. The sidebar Issues nav is
        // always shown as a Core feature with no addons flag. Enum filters bind as nullable strings parsed
        // case-insensitively so the SPA can omit them — a nullable int query param would reject a bad value.
        apiGroup.MapGet("issues", async (
            [FromServices] IErrorGroupQueryService svc,
            [FromQuery] string? source,
            [FromQuery] string? status,
            [FromQuery] string? application,
            [FromQuery] string? kind,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var parsedSource = Enum.TryParse<ErrorSource>(source, ignoreCase: true, out var s) ? s : (ErrorSource?)null;
            var parsedStatus = Enum.TryParse<ErrorGroupStatus>(status, ignoreCase: true, out var st) ? st : (ErrorGroupStatus?)null;
            var parsedKind = Enum.TryParse<ErrorKind>(kind, ignoreCase: true, out var k) ? k : (ErrorKind?)null;

            return Results.Ok(await svc.GetGroups(parsedSource, parsedStatus, application, parsedKind, page ?? 0, pageSize ?? 50, ct));
        });

        apiGroup.MapGet("issues/{fingerprint}", async ([FromServices] IErrorGroupQueryService svc, string fingerprint, CancellationToken ct) =>
        {
            var detail = await svc.GetGroup(fingerprint, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapPost("issues/{fingerprint}/status", async ([FromServices] IErrorGroupCommandService svc, string fingerprint, [FromBody] ErrorGroupStatusRequest body, CancellationToken ct) =>
        {
            var updated = await svc.SetStatus(fingerprint, body.Status, ct);

            return updated ? Results.NoContent() : Results.NotFound();
        });

        // Webhooks — durable outbound delivery. IWebhookQueryService / IWebhookCommandService are always
        // registered by AddWarp (dashboard-only processes resolve them), so these data routes are
        // non-nullable; the sidebar nav is gated on the addons flag (IWebhookRedeliveryEnqueuer presence),
        // not on a 404. Attempt timelines ride on the detail payload (AdapterCallLog via CorrelationId).
        apiGroup.MapGet("webhooks", async (
            [FromServices] IWebhookQueryService svc,
            [FromQuery] int? status,
            [FromQuery] string? eventType,
            [FromQuery] string? reference,
            [FromQuery] string? group,
            [FromQuery] DateTime? since,
            [FromQuery] DateTime? until,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            CancellationToken ct) =>
        {
            var filter = new WebhookDeliveryFilter
            {
                Status = status.HasValue ? (WebhookDeliveryStatus?)status.Value : null,
                EventType = eventType,
                Reference = reference,
                GroupName = group,
                Since = since,
                Until = until,
                Page = page ?? 0,
                PageSize = pageSize ?? 20,
            };

            return Results.Ok(await svc.GetDeliveries(filter, ct));
        });

        apiGroup.MapGet("webhooks/groups", async (
            [FromServices] IWebhookQueryService svc,
            [FromQuery] string? by,
            CancellationToken ct) =>
        {
            var dimension = string.Equals(by, "endpoint", StringComparison.OrdinalIgnoreCase)
                ? WebhookGroupBy.Endpoint
                : WebhookGroupBy.EventType;

            return Results.Ok(await svc.GetGroups(dimension, ct));
        });

        apiGroup.MapGet("webhooks/history", async (
            [FromServices] IWebhookQueryService svc,
            [FromQuery] string? eventType,
            [FromQuery] string? group,
            CancellationToken ct) =>
        {
            var filter = new WebhookDeliveryFilter { EventType = eventType, GroupName = group };

            return Results.Ok(await svc.GetDeliveryHistory(filter, ct));
        });

        apiGroup.MapGet("webhooks/summary", async ([FromServices] IWebhookQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetSummary(ct)));

        apiGroup.MapGet("webhooks/{id}", async ([FromServices] IWebhookQueryService svc, Guid id, CancellationToken ct) =>
        {
            var detail = await svc.GetDeliveryDetail(id, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapPost("webhooks/{id}/redeliver", async ([FromServices] IWebhookCommandService svc, Guid id, CancellationToken ct) =>
        {
            var result = await svc.Redeliver(id, ct);

            return result switch
            {
                WebhookRedeliveryResult.Enqueued => Results.Ok(),
                WebhookRedeliveryResult.NotFound => Results.NotFound(),
                WebhookRedeliveryResult.Rejected => Results.Conflict(
                    new { message = "Delivery is already pending — it already has a live executor job." }),
                WebhookRedeliveryResult.Unavailable => Results.Conflict(
                    new { message = "Redelivery is unavailable in this process — no webhooks worker is wired here. Redeliver from a server host running AddWebhooks()." }),
                _ => Results.NotFound(),
            };
        });

        apiGroup.MapGet("services", async ([FromServices] IBackgroundServiceQueryService svc, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);

            return Results.Ok(list);
        });

        apiGroup.MapGet("services/{name}", async ([FromServices] IBackgroundServiceQueryService svc, string name, CancellationToken ct) =>
        {
            var detail = await svc.GetAsync(name, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapGet("services/{name}/logs", async (
            [FromServices] IBackgroundServiceQueryService svc,
            string name,
            [FromQuery] BackgroundServiceLogSource? source,
            [FromQuery] int? level,
            [FromQuery] long? fromId,
            [FromQuery] int? limit,
            CancellationToken ct) =>
        {
            var minLevel = level.HasValue ? (Microsoft.Extensions.Logging.LogLevel?)level.Value : null;
            var effectiveLimit = Math.Min(limit ?? 100, 500);
            var logs = await svc.GetLogsAsync(name, source, minLevel, fromId, effectiveLimit, ct);

            return Results.Ok(logs);
        });

        apiGroup.MapGet("services/{name}/lease", async ([FromServices] IBackgroundServiceQueryService svc, string name, CancellationToken ct) =>
        {
            var lease = await svc.GetLeaseAsync(name, ct);

            return lease is null ? Results.NotFound() : Results.Ok(lease);
        });

        // Applications — multi-app observability roster (§8.19). IApplicationQueryService is always
        // registered by AddWarp (dashboard-only processes resolve it), so these routes are non-nullable; the
        // Applications page is the renamed Servers page and is always shown. The {id} is the URL-safe base64
        // of the application name (UrlSafeId.Encode/TryDecode — the shared codec, also used by the endpoints
        // detail route).
        apiGroup.MapGet("applications", async ([FromServices] IApplicationQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetApplications(ct)));

        apiGroup.MapGet("applications/{id}", async ([FromServices] IApplicationQueryService svc, string id, CancellationToken ct) =>
        {
            var application = UrlSafeId.TryDecode(id);
            if (application is null)
            {
                return Results.NotFound();
            }

            var detail = await svc.GetApplicationDetail(application, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        apiGroup.MapGet("applications/{id}/instances/{instanceId}", async ([FromServices] IApplicationQueryService svc, string id, Guid instanceId, CancellationToken ct) =>
        {
            var application = UrlSafeId.TryDecode(id);
            if (application is null)
            {
                return Results.NotFound();
            }

            var detail = await svc.GetInstanceDetail(application, instanceId, ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        // Global job execution metrics (by-type / by-handler, from the durable Statistic aggregates, so they
        // survive Job-row cleanup). Optional ?application= narrows to a single executor application. The model
        // carries both ByType and ByHandler; the Jobs-by-Type page toggles between them client-side.
        apiGroup.MapGet("jobs/metrics", async ([FromServices] IJobQueryService svc, [FromQuery] string? application) =>
            Results.Ok(await svc.GetJobExecutionMetrics(application)));

        // Per-queue SLIs: queue-wait latency (avg + p95/p99, from the durable qwait fold) + latest backlog
        // depth/oldest-age (§8.26). Optional ?application= narrows to a single executor application. Always
        // registered (IJobQueryService is always registered by AddWarp), like jobs/metrics.
        apiGroup.MapGet("queues/metrics", async ([FromServices] IJobQueryService svc, [FromQuery] string? application) =>
            Results.Ok(await svc.GetQueueMetrics(application)));

        // Per-app job execution metrics (by-type / by-handler, from the durable Statistic aggregates).
        apiGroup.MapGet("applications/{id}/jobstats", async ([FromServices] IJobQueryService svc, string id) =>
        {
            var application = UrlSafeId.TryDecode(id);

            return application is null
                ? Results.NotFound()
                : Results.Ok(await svc.GetJobExecutionMetrics(application));
        });

        // Extension manifests
        var manifests = extensions.ConvertAll(x => x.GetManifest());
        apiGroup.MapGet("extensions", () => manifests);

        // Extension API endpoints (each under /ext/{name}/, auth-protected)
        foreach (var ext in extensions)
        {
            var extGroup = apiGroup.MapGroup($"ext/{ext.Name}");
            ext.MapEndpoints(extGroup);
        }
    }
}

public sealed record ErrorGroupStatusRequest(ErrorGroupStatus Status);

public sealed record UpsertConcurrencyLimitRequest(string Name, int Limit);

public sealed record UpdateConcurrencyLimitRequest(int Limit);

public sealed record UpsertRateLimitRequest(string Name, int Count, int WindowSeconds);

public sealed record UpdateRateLimitRequest(int Count, int WindowSeconds);
