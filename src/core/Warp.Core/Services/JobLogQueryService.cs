using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads the <see cref="JobLog"/> rows a handler produces while it runs: its log output and its
/// reported progress.
/// </summary>
/// <remarks>
/// Separate from <see cref="IJobQueryService"/> so adding it does not break hosts that implement or
/// decorate that interface. Both series grow with the job, which is why neither is returned by
/// <see cref="IJobQueryService.GetJobDetailById"/>: output is paged, progress is read per bar.
/// </remarks>
public interface IJobLogQueryService
{
    /// <summary>
    /// One page of the job's handler output, newest first. <c>PageSize</c> is clamped to
    /// 1..<see cref="JobLogQueryService{TContext}.MaxPageSize"/> and a negative <c>Page</c> reads as 0.
    /// </summary>
    Task<PagedSlice<JobLogModel>> GetHandlerLogs(Guid jobId, BaseListRequest request);

    /// <summary>
    /// The current value of each progress bar the job reported, ordered by bar name.
    /// </summary>
    Task<List<JobProgressModel>> GetProgress(Guid jobId);
}

public class JobLogQueryService<TContext> : IJobLogQueryService
    where TContext : DbContext
{
    /// <summary>
    /// Upper bound on one page. The endpoint takes the page size from the query string, and an
    /// unbounded one would return the whole log set this paging exists to avoid.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// The host's context; handler output and progress are read where the dashboard reads everything else.
    /// </summary>
    private readonly TContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobLogQueryService{TContext}"/> class over the host's
    /// <typeparamref name="TContext"/>.
    /// </summary>
    public JobLogQueryService(TContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Served by the (JobId, EventType, Timestamp) index on job_log, scanned backwards. One row past
    /// the page answers "is there an older page" without counting the job's whole output. Id breaks
    /// timestamp ties so Skip/Take is stable; the collector stamps a job's lines strictly increasing,
    /// so ties only arise between rows from different sources.
    /// </remarks>
    public async Task<PagedSlice<JobLogModel>> GetHandlerLogs(Guid jobId, BaseListRequest request)
    {
        var bounded = new BaseListRequest
        {
            Page = Math.Max(0, request.Page),
            PageSize = Math.Clamp(request.PageSize, 1, MaxPageSize),
        };

        return await _context.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == JobLogEventTypes.Log)
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Select(x =>
                new JobLogModel
                {
                    Id = x.Id,
                    EventType = x.EventType,
                    Timestamp = x.Timestamp,
                    Level = x.Level,
                    Message = x.Message,
                    Exception = x.Exception,
                    DurationMs = x.DurationMs,
                    WorkerId = x.WorkerId,
                })
            .ToPagedSliceAsync(bounded);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The worker appends a row per changed value, so the series grows with the job; only the newest
    /// row per bar is read. Cheap enough for the dashboard to poll while the job is running, which is
    /// why it is not part of the job detail. Grouped on the same coalesced name it returns, so a null
    /// and an empty name cannot come back as two bars both called "".
    /// </remarks>
    public async Task<List<JobProgressModel>> GetProgress(Guid jobId)
    {
        var bars = await _context.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == JobLogEventTypes.Progress)
            .Where(x => x.Value != null)
            .GroupBy(x => x.Name ?? string.Empty)
            .Select(x => x
                .OrderByDescending(y => y.Timestamp)
                .ThenByDescending(y => y.Id)
                .Select(y =>
                    new JobProgressModel
                    {
                        Name = x.Key,
                        Value = y.Value!.Value,
                        UpdatedAt = y.Timestamp,
                    })
                .First())
            .ToListAsync();

        return [.. bars.OrderBy(x => x.Name, StringComparer.Ordinal)];
    }
}
