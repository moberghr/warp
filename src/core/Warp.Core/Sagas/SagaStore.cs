using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Events;
using Warp.Core.Notifications;

namespace Warp.Core.Sagas;

// Schema-evolution policy: when a user renames or removes a property on their saga subclass,
// existing rows on disk carry the old shape. Skip unmapped members so the load proceeds with
// defaults for the absent fields rather than throwing. The trade-off is silent data loss on
// the dropped property — acceptable because (a) the alternative is breaking every persisted
// saga on a property rename, and (b) the user controls their own saga state shape, so they
// know what they removed. Documented in website/docs/features/sagas.md.
//
// Lives in a non-generic class because S2743 flags `static readonly` fields on generic types
// (one copy per closed generic).
internal static class SagaStoreJsonOptions
{
    public static readonly JsonSerializerOptions Deserialize = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}

/// <summary>
/// Persists saga rows. JSON serialization uses default options on the serialize side (matching
/// <c>Publisher.cs</c>) and <see cref="JsonUnmappedMemberHandling.Skip"/> on the deserialize side
/// (schema-evolution tolerance). State is round-tripped via the runtime type of the in-memory
/// saga, so subclass properties serialize correctly.
/// </summary>
/// <remarks>
/// <c>SaveChangesAsync</c> mirrors <c>Publisher.SaveChangesAsync</c>: it captures pending
/// <c>JobEnqueued</c>/<c>MessageEnqueued</c> notifications from any <c>IPublisher.Publish/Enqueue</c>
/// calls the saga handler made, commits, and then fires the notifications. Skipping this would
/// silently drop push notifications because the entries become <c>Unchanged</c> after commit and
/// the worker's later <c>CapturePending</c> would see nothing — saga-published children would
/// fall back to polling.
/// </remarks>
public sealed class SagaStore<TContext> : ISagaStore
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;
    private readonly ServerTaskSignals<TContext> _signals;

    public SagaStore(
        TContext context,
        TimeProvider time,
        IWarpNotificationTransport notificationTransport,
        IDatabaseExceptionClassifier exceptionClassifier,
        ServerTaskSignals<TContext> signals)
    {
        _context = context;
        _time = time;
        _notificationTransport = notificationTransport;
        _exceptionClassifier = exceptionClassifier;
        _signals = signals;
    }

    public async Task<TSaga?> Load<TSaga>(string correlationKey, CancellationToken cancellationToken)
        where TSaga : Saga, new()
    {
        var typeName = TypeNameOf<TSaga>();

        var row = await _context.Set<SagaState>()
            .Where(x => x.Type == typeName)
            .Where(x => x.CorrelationKey == correlationKey)
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null)
        {
            return null;
        }

        var saga = JsonSerializer.Deserialize<TSaga>(row.StateJson, SagaStoreJsonOptions.Deserialize)
            ?? throw new InvalidOperationException(
                $"Saga state for '{typeName}' with correlation '{correlationKey}' deserialized to null.");

        saga.Id = row.Id;
        saga.CorrelationKey = row.CorrelationKey;

        return saga;
    }

    public void Add<TSaga>(TSaga saga)
        where TSaga : Saga
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var row = new SagaState
        {
            Id = saga.Id,
            Type = TypeNameOf(saga),
            CorrelationKey = saga.CorrelationKey,
            StateJson = JsonSerializer.Serialize(saga, saga.GetType()),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _context.Set<SagaState>().Add(row);
    }

    public void Update<TSaga>(TSaga saga)
        where TSaga : Saga
    {
        var row = TrackedRowFor(saga)
            ?? throw new InvalidOperationException(
                $"Cannot Update saga '{TypeNameOf(saga)}' with id {saga.Id} — row not in change tracker. " +
                "Did you call Load before Update?");

        // Always re-serialize and bump UpdatedAt, even when the handler made no observable state
        // change. UpdatedAt is the operational "last touched by a saga message" signal — operators
        // query it to find stuck sagas ("WHERE updated_at < now() - INTERVAL '7 days'"). Skipping
        // the write on no-op handler branches would leave UpdatedAt stale on actively-processing
        // sagas, masking real "stuck" cases. The marginal write amplification is acceptable.
        row.StateJson = JsonSerializer.Serialize(saga, saga.GetType());
        row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
    }

    public void Remove<TSaga>(TSaga saga)
        where TSaga : Saga
    {
        var row = TrackedRowFor(saga)
            ?? throw new InvalidOperationException(
                $"Cannot Remove saga '{TypeNameOf(saga)}' with id {saga.Id} — row not in change tracker.");

        _context.Set<SagaState>().Remove(row);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var pending = NotificationDispatch.CapturePending(_context);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // EF Core does not detach the failed entities — the Modified Saga and any Added rows
            // from IPublisher.Publish() called inside the handler stay tracked. The worker's
            // outbox SaveChanges would re-throw on the same saga row, masking the requeue outcome
            // the proxy is about to set. Drop everything so the worker commits an empty change set.
            _context.ChangeTracker.Clear();
            throw new SagaSaveConflictException(SagaSaveConflictKind.Version, ex);
        }
        catch (DbUpdateException ex) when (_exceptionClassifier.IsUniqueConstraintViolation(ex))
        {
            // Two [StartsSaga] messages for the same correlation key raced past the mutex (e.g.
            // transient lock-provider hiccup gave both a "fresh" handle). Both reached the Add
            // path; one won the SaveChanges, the loser hits the (Type, CorrelationKey) unique
            // index. Same recovery as version conflict — clear tracker, requeue, next attempt
            // will Load() the freshly-committed saga and merge.
            _context.ChangeTracker.Clear();
            throw new SagaSaveConflictException(SagaSaveConflictKind.UniqueConstraint, ex);
        }

        await NotificationDispatch.DispatchAsync(pending, _signals, _notificationTransport, cancellationToken);
    }

    public void DiscardPendingChanges() => _context.ChangeTracker.Clear();

    public void RecordCounterDelta(string key, int value)
    {
        _context.Set<Counter>().Add(new Counter { Key = key, Value = value });
    }

    public void RecordJobLink(Guid sagaId, Guid jobId)
    {
        _context.Set<SagaJobLink>().Add(new SagaJobLink
        {
            SagaId = sagaId,
            JobId = jobId,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        });
    }

    public async Task RemoveLinksForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
    {
        // Detach any link rows the current invocation just Added — they'd otherwise be in the
        // change tracker as Added AND we're about to load-and-Remove them via a different code
        // path, which EF Core resolves as "no-op" but the load returns nothing since they're not
        // persisted yet. Detaching makes the load+remove sequence behave consistently.
        var pendingAdded = _context.ChangeTracker.Entries<SagaJobLink>()
            .Where(e => e.Entity.SagaId == sagaId && e.State == EntityState.Added)
            .ToList();
        foreach (var entry in pendingAdded)
        {
            entry.State = EntityState.Detached;
        }

        // Load the persisted link rows into the change tracker and stage RemoveRange. The deletes
        // commit atomically with the saga's removal in the proxy's SaveChangesAsync — if the
        // saga save fails (DbUpdateConcurrencyException), the link deletes roll back too.
        var existing = await _context.Set<SagaJobLink>()
            .Where(l => l.SagaId == sagaId)
            .ToListAsync(cancellationToken);

        _context.Set<SagaJobLink>().RemoveRange(existing);
    }

    private SagaState? TrackedRowFor<TSaga>(TSaga saga)
        where TSaga : Saga
    {
        return _context.ChangeTracker.Entries<SagaState>()
            .Select(e => e.Entity)
            .FirstOrDefault(x => x.Id == saga.Id);
    }

    private static string TypeNameOf<TSaga>() => typeof(TSaga).FullName!;

    private static string TypeNameOf<TSaga>(TSaga instance)
        where TSaga : Saga
        => instance.GetType().FullName!;
}
