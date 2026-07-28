using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads a single trace as a unified set of spans (§8.28), unioned from the rows Warp already persists —
/// <see cref="ClientEventLog"/> (browser request), <see cref="EndpointCallLog"/> (server request),
/// <see cref="Job"/> (jobs, tree via <c>SpawnedByJobId</c>), and <see cref="AdapterCallLog"/> (outbound calls).
/// Each is already a span; there is no separate span store. Registered by <c>AddWarp</c>, so any process
/// (dashboard/publisher-only) resolves it.
/// </summary>
public interface ITraceQueryService
{
    Task<TraceOverviewModel?> GetTrace(Guid traceId, CancellationToken ct);
}

public sealed class TraceQueryService<TContext> : ITraceQueryService
    where TContext : DbContext
{
    // A trace's rows are unbounded (a batch/message fans out to N children sharing one TraceId). Cap each
    // source so a huge fan-out can't load thousands of rows or overwhelm the waterfall render; surface the
    // cap via IsTruncated (the sagas/endpoint-recent-calls pattern) rather than silently dropping.
    private const int MaxSpansPerSource = 500;

    private readonly TContext _context;

    public TraceQueryService(TContext context) => _context = context;

    public async Task<TraceOverviewModel?> GetTrace(Guid traceId, CancellationToken ct)
    {
        var spans = new List<TraceSpanModel>();
        var truncated = false;

        var clients = await _context.Set<ClientEventLog>()
            .AsNoTracking()
            .Where(x => x.TraceId == traceId)
            .OrderBy(x => x.Timestamp)
            .Take(MaxSpansPerSource + 1)
            .Select(x => new { x.Id, x.Type, x.Name, x.Url, x.Value, x.Timestamp })
            .ToListAsync(ct);
        truncated |= Trim(clients);
        foreach (var c in clients)
        {
            spans.Add(new TraceSpanModel
            {
                Source = "client",
                Id = c.Id,
                Name = c.Name ?? c.Url ?? "request",
                StartTime = c.Timestamp,
                DurationMs = c.Value,
                Status = c.Type.ToString(),
                IsError = c.Type == ClientEventType.Error,
            });
        }

        var endpoints = await _context.Set<EndpointCallLog>()
            .AsNoTracking()
            .Where(x => x.TraceId == traceId)
            .OrderBy(x => x.Timestamp)
            .Take(MaxSpansPerSource + 1)
            .Select(x => new { x.Id, x.Method, x.RouteTemplate, x.Outcome, x.DurationMs, x.Timestamp })
            .ToListAsync(ct);
        truncated |= Trim(endpoints);
        foreach (var e in endpoints)
        {
            spans.Add(new TraceSpanModel
            {
                Source = "endpoint",
                Id = e.Id,
                Name = $"{e.Method} {e.RouteTemplate}",
                StartTime = e.Timestamp,
                DurationMs = e.DurationMs,
                Status = e.Outcome.ToString(),
                IsError = e.Outcome != AdapterCallOutcome.Success,
            });
        }

        var jobs = await _context.Set<Job>()
            .AsNoTracking()
            .Where(x => x.TraceId == traceId)
            .OrderBy(x => x.CreateTime)
            .Take(MaxSpansPerSource + 1)
            .Select(x => new { x.Id, x.Type, x.CurrentState, x.SpawnedByJobId, x.CreateTime })
            .ToListAsync(ct);
        truncated |= Trim(jobs);

        // The Job row has no execution-duration column, but the worker already persists it: each terminal JobLog
        // (Completed/Failed/Cancelled) carries the handler's DurationMs at its Timestamp. Two-step fetch (job ids
        // → their duration-bearing logs, §5.2) gives a real bar per job — start = terminal timestamp − duration
        // — so a job nests correctly over the adapter calls it made. Falls back to the CreateTime marker when the
        // logs have aged out (retention) or the job hasn't finished. Retries: the latest execution wins.
        var jobIds = jobs.ConvertAll(x => x.Id);
        var executions = await _context.Set<JobLog>()
            .AsNoTracking()
            .Where(x => jobIds.Contains(x.JobId))
            .Where(x => x.DurationMs != null)
            .Select(x => new { x.JobId, x.Timestamp, x.DurationMs })
            .ToListAsync(ct);
        var latestExecutionByJob = executions
            .GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => x.MaxBy(y => y.Timestamp)!);

        foreach (var j in jobs)
        {
            var execution = latestExecutionByJob.GetValueOrDefault(j.Id);
            var duration = execution?.DurationMs;

            spans.Add(new TraceSpanModel
            {
                Source = "job",
                Id = j.Id,
                Name = j.Type ?? "job",
                StartTime = execution is null ? j.CreateTime : execution.Timestamp.AddMilliseconds(-execution.DurationMs!.Value),
                DurationMs = duration,
                Status = j.CurrentState.ToString(),
                IsError = j.CurrentState == State.Failed,
                ParentId = j.SpawnedByJobId,
            });
        }

        // AdapterCallLog stores the trace id as the 32-hex string, not a Guid.
        var traceHex = traceId.ToString("N");
        var adapters = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.TraceId == traceHex)
            .OrderBy(x => x.Timestamp)
            .Take(MaxSpansPerSource + 1)
            .Select(x => new { x.Id, x.AdapterName, x.Operation, x.Outcome, x.DurationMs, x.Timestamp })
            .ToListAsync(ct);
        truncated |= Trim(adapters);
        foreach (var a in adapters)
        {
            spans.Add(new TraceSpanModel
            {
                Source = "adapter",
                Id = a.Id,
                Name = $"{a.AdapterName}.{a.Operation}",
                StartTime = a.Timestamp,
                DurationMs = a.DurationMs,
                Status = a.Outcome.ToString(),
                IsError = a.Outcome != AdapterCallOutcome.Success,
            });
        }

        if (spans.Count == 0)
        {
            return null;
        }

        return new TraceOverviewModel
        {
            TraceId = traceId,
            Spans = [.. spans.OrderBy(x => x.StartTime)],
            ClientCount = clients.Count,
            EndpointCount = endpoints.Count,
            JobCount = jobs.Count,
            AdapterCount = adapters.Count,
            ErrorCount = spans.Count(x => x.IsError),
            IsTruncated = truncated,
        };
    }

    private static bool Trim<T>(List<T> rows)
    {
        if (rows.Count <= MaxSpansPerSource)
        {
            return false;
        }

        rows.RemoveRange(MaxSpansPerSource, rows.Count - MaxSpansPerSource);

        return true;
    }
}
