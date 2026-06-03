using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Sagas;

namespace Warp.Core.Services;

public interface ISagaCommandService
{
    /// <summary>
    /// Force-deletes the saga row and all its <see cref="SagaJobLink"/> entries.
    /// Operator-initiated equivalent of <c>MarkCompleted</c> — used to clean up sagas the user's
    /// handler can no longer reach. In-flight messages for this correlation will hit
    /// <c>NotFoundAsync</c> on the next attempt.
    /// </summary>
    /// <remarks>
    /// Acquires the saga mutex (<c>warp:saga:{Type}:{CorrelationKey}</c>) for up to
    /// <see cref="SagaCommandServiceConstants.ForceCompleteMutexTimeout"/> before deleting so an in-flight handler on
    /// another worker doesn't lose its commit to a race. Emits a structured log entry
    /// (<c>LogLevel.Information</c>) recording the saga's id, type, and correlation key for
    /// audit purposes — the row itself is gone after this call, so the log is the only trail.
    /// </remarks>
    Task<bool> ForceComplete(Guid sagaId);
}

/// <summary>
/// Tuning constants for <see cref="SagaCommandService{TContext}"/>. Lifted out of the generic
/// type so the runtime doesn't allocate one copy per closed generic (S2743).
/// </summary>
internal static class SagaCommandServiceConstants
{
    /// <summary>
    /// How long <c>ForceComplete</c> waits to acquire the saga mutex before giving up.
    /// Keep it short: an in-flight handler iteration averages well under this; if it takes
    /// longer the saga is genuinely stuck and the operator should investigate that instead.
    /// </summary>
    public static readonly TimeSpan ForceCompleteMutexTimeout = TimeSpan.FromSeconds(5);
}

public class SagaCommandService<TContext> : ISagaCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IWarpLockProvider _lockProvider;
    private readonly ILogger<SagaCommandService<TContext>> _logger;

    public SagaCommandService(
        TContext context,
        IWarpLockProvider lockProvider,
        ILogger<SagaCommandService<TContext>> logger)
    {
        _context = context;
        _lockProvider = lockProvider;
        _logger = logger;
    }

    public async Task<bool> ForceComplete(Guid sagaId)
    {
        // Read the saga's Type + CorrelationKey first so we know which mutex to take. Unique
        // index makes this a single index scan. We re-read inside the lock to defeat the
        // "operator force-completes a saga that just finished" race.
        var saga = await _context.Set<SagaState>().FirstOrDefaultAsync(s => s.Id == sagaId);
        if (saga == null)
        {
            return false;
        }

        var lockName = $"warp:saga:{saga.Type}:{saga.CorrelationKey}";
        await using var handle = await _lockProvider.TryAcquireAsync(
            lockName,
            SagaCommandServiceConstants.ForceCompleteMutexTimeout,
            CancellationToken.None);

        if (handle == null)
        {
            // An in-flight handler is still holding the lock after the timeout. Refuse —
            // the operator's "force complete" expectation is "do it now, atomically"; coming
            // back as a no-op after 5 seconds of waiting is the honest answer rather than
            // racing the handler.
            _logger.LogWarning(
                "Force-complete on saga {SagaId} (type {SagaType}, key {CorrelationKey}) aborted: " +
                "mutex held by in-flight handler beyond {Timeout}.",
                sagaId,
                saga.Type,
                saga.CorrelationKey,
                SagaCommandServiceConstants.ForceCompleteMutexTimeout);

            return false;
        }

        // Re-read inside the lock — the saga may have completed naturally between the first
        // read and us acquiring the lock. Use a fresh tracking entry to avoid stale Version.
        _context.ChangeTracker.Clear();
        var fresh = await _context.Set<SagaState>().FirstOrDefaultAsync(s => s.Id == sagaId);
        if (fresh == null)
        {
            return false;
        }

        // Load the link rows into the change tracker and stage them for removal so the deletes
        // ride the same SaveChanges transaction as the saga removal. Avoids the orphan-link
        // scenario where a separate ExecuteDelete commits the link deletes but the saga delete
        // subsequently fails (transient retry, etc.).
        var links = await _context.Set<SagaJobLink>()
            .Where(l => l.SagaId == sagaId)
            .ToListAsync();

        _context.Set<SagaJobLink>().RemoveRange(links);
        _context.Set<SagaState>().Remove(fresh);

        // Counter writes commit in the same SaveChanges as the deletion. Bucketed by hour for
        // the historical chart on the dashboard's Counters page; the operational signal is
        // "how often did an operator have to step in?".
        var now = DateTime.UtcNow;
        var hour = now.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);
        _context.Set<Counter>().Add(new Counter { Key = "stats:saga_force_completed", Value = 1 });
        _context.Set<Counter>().Add(new Counter { Key = $"stats:saga_force_completed:{hour}", Value = 1 });

        await _context.SaveChangesAsync();

        // Structured-log audit. The row is gone; this is the only record that the saga existed
        // and was force-completed.
        _logger.LogInformation(
            "Saga {SagaId} (type {SagaType}, key {CorrelationKey}) force-completed by operator. " +
            "{LinkCount} job link(s) removed.",
            sagaId,
            fresh.Type,
            fresh.CorrelationKey,
            links.Count);

        return true;
    }
}
