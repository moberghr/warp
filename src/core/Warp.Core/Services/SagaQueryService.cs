using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

public interface ISagaQueryService
{
    Task<PagedList<SagaListItemModel>> GetSagas(BaseListRequest request, string? type, string? correlationKeyContains);

    Task<SagaDetailModel?> GetSagaById(Guid id);

    Task<SagaActivityResponseModel> GetSagaActivity(Guid id);

    Task<IReadOnlyList<string>> GetSagaTypes();

    Task<SagaStatsModel> GetStats();
}

public class SagaQueryService<TContext> : ISagaQueryService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    public SagaQueryService(TContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<PagedList<SagaListItemModel>> GetSagas(BaseListRequest request, string? type, string? correlationKeyContains)
    {
        var query = _context.Set<SagaState>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(s => s.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(correlationKeyContains))
        {
            // Escape SQL LIKE wildcards in the user-supplied filter so a search for "%" doesn't
            // degenerate into a full table scan over the saga rows. The pattern uses backslash
            // as the escape char — same convention on PostgreSQL (default) and SQL Server (via
            // ESCAPE clause). EF Core translates EF.Functions.Like to a parameterized LIKE so
            // injection is not a concern; the issue here is plan quality, not safety.
            var pattern = "%" + EscapeLikePattern(correlationKeyContains) + "%";
            query = query.Where(s => EF.Functions.Like(s.CorrelationKey, pattern, "\\"));
        }

        return await query
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new SagaListItemModel
            {
                Id = s.Id,
                Type = s.Type,
                CorrelationKey = s.CorrelationKey,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
            })
            .ToPagedListAsync(request);
    }

    public async Task<SagaDetailModel?> GetSagaById(Guid id)
    {
        return await _context.Set<SagaState>()
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new SagaDetailModel
            {
                Id = s.Id,
                Type = s.Type,
                CorrelationKey = s.CorrelationKey,
                StateJson = s.StateJson,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
                Version = s.Version,
            })
            .FirstOrDefaultAsync();
    }

    public async Task<SagaActivityResponseModel> GetSagaActivity(Guid id)
    {
        // Indexed lookup on (SagaId, CreatedAt) → time-ordered JobIds. Then one round trip each
        // for the Job rows and JobLog rows. No JSON parsing, no LIKE filters.
        //
        // Cap at 200 most-recent invocations. Without this, a long-lived saga with thousands of
        // correlated messages would hit SQL Server's ~2100 parameter limit on the Contains()
        // filters below and throw. 200 is generous for a UI: the dashboard shows the most
        // recent activity, not the full lifetime history.
        const int MaxActivityRows = 200;

        // The page shows the last few lifecycle events of each invocation. Handler output and progress
        // grow with the handler (tens of thousands of rows for a chatty one) and are read on the job's
        // own detail page, so they are never loaded here.
        const int MaxLogsPerInvocation = 5;

        var totalInvocations = await _context.Set<SagaJobLink>()
            .AsNoTracking()
            .LongCountAsync(l => l.SagaId == id);

        var jobIds = await _context.Set<SagaJobLink>()
            .AsNoTracking()
            .Where(l => l.SagaId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(MaxActivityRows)
            .OrderBy(l => l.CreatedAt)
            .Select(l => l.JobId)
            .ToListAsync();

        if (jobIds.Count == 0)
        {
            return new SagaActivityResponseModel
            {
                Entries = [],
                TotalInvocations = totalInvocations,
                IsTruncated = false,
            };
        }

        var jobs = await _context.Set<Job>()
            .AsNoTracking()
            .Where(j => jobIds.Contains(j.Id))
            .Select(j => new
            {
                j.Id,
                MessageType = j.Type,
                JobState = j.CurrentState,
                j.CreateTime,
            })
            .ToListAsync();

        var logs = await _context.Set<JobLog>()
            .AsNoTracking()
            .Where(l => jobIds.Contains(l.JobId))
            .Where(l => l.EventType != JobLogEventTypes.Log)
            .Where(l => l.EventType != JobLogEventTypes.Progress)
            .OrderBy(l => l.Timestamp)
            .Select(l => new
            {
                l.JobId,
                Log = new JobLogModel
                {
                    Id = l.Id,
                    EventType = l.EventType,
                    Timestamp = l.Timestamp,
                    Level = l.Level,
                    Message = l.Message,
                    DurationMs = l.DurationMs,
                    WorkerId = l.WorkerId,
                },
            })
            .ToListAsync();

        var logsByJob = logs
            .GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Log).TakeLast(MaxLogsPerInvocation).ToList());
        var jobsById = jobs.ToDictionary(j => j.Id);

        var entries = jobIds
            .Where(jobsById.ContainsKey)
            .Select(jid => new SagaActivityEntryModel
            {
                JobId = jid,
                MessageType = ShortTypeName(jobsById[jid].MessageType) ?? string.Empty,
                JobState = jobsById[jid].JobState.ToString(),
                CreateTime = jobsById[jid].CreateTime,
                Logs = logsByJob.TryGetValue(jid, out var l) ? l : [],
            })
            .ToList();

        return new SagaActivityResponseModel
        {
            Entries = entries,
            TotalInvocations = totalInvocations,
            IsTruncated = totalInvocations > MaxActivityRows,
        };
    }

    public async Task<IReadOnlyList<string>> GetSagaTypes()
    {
        return await _context.Set<SagaState>()
            .AsNoTracking()
            .Select(s => s.Type)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();
    }

    public async Task<SagaStatsModel> GetStats()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        var live = await _context.Set<SagaState>().LongCountAsync();
        var startedToday = await _context.Set<SagaState>()
            .Where(s => s.CreatedAt >= todayStart)
            .LongCountAsync();

        return new SagaStatsModel
        {
            LiveSagas = live,
            StartedToday = startedToday,

            // No "completed today" counter yet — sagas are deleted on completion, so there's no
            // historical row to count. CompletedToday stays 0 until we add a separate audit table.
            CompletedToday = 0,
        };
    }

    private static string EscapeLikePattern(string input)
    {
        // Order matters: escape the backslash before injecting backslashes for % and _.
        return input
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    // Local helper rather than WarpTelemetry.GetShortTypeName: the dashboard wants the
    // unqualified class name ("OrderCreated"), but the telemetry helper deliberately keeps
    // the full namespace prefix so OTel exports stay disambiguated when two assemblies define
    // the same class. Different consumer, different requirement.
    private static string? ShortTypeName(string? assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return null;
        }

        var commaIndex = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);
        var typeName = commaIndex > 0 ? assemblyQualifiedName[..commaIndex] : assemblyQualifiedName;

        var dotIndex = typeName.LastIndexOf('.');

        return dotIndex > 0 ? typeName[(dotIndex + 1)..] : typeName;
    }
}
