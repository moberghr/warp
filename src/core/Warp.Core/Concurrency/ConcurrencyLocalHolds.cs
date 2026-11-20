using System.Collections.Concurrent;

namespace Warp.Core.Concurrency;

/// <summary>
/// How many slots of each concurrency key THIS process currently holds, per acquire request.
/// <para>
/// Every hold counted here is a real database hold, so when this process alone already holds
/// <c>limit</c> slots taken with the same request, the database acquire is certain to fail — the refusal
/// can be decided without the round trip (and without opening a lock-pool connection, §2.18). The answer
/// is exact, not a heuristic: it can only say "full" when the key IS full. A hold taken by another process
/// is invisible here, so those refusals still go to the database exactly as before.
/// </para>
/// <para>
/// Holds are counted per (lock name, limit) — the exact acquire request — never per name alone. The
/// providers do not lock the same thing for every limit: on PostgreSQL a mutex locks <c>name</c> and a
/// semaphore of N locks <c>name:0..name:{N-1}</c>, independent caps by design (§8.6). Counted per name, N
/// semaphore slots would refuse a mutex on the same key whose lock is free, and a Skip-mode mutex job would
/// be deleted. Per request, a mutex and a semaphore on one key — or a key whose limit an admin changed
/// mid-run — never share a count and simply go to the database.
/// </para>
/// <para>
/// The count is released BEFORE the database lock, never after: the opposite order would leave a moment
/// where the key is free in the database but still counted here, and a Skip-mode job arriving in it would
/// be deleted although it could have run. Released first, the worst case is a database attempt that
/// fails — what happened before this existed.
/// </para>
/// </summary>
internal sealed class ConcurrencyLocalHolds
{
    private readonly ConcurrentDictionary<(string LockName, int Limit), int> _held = new();

    public bool IsSaturated(string lockName, int limit) =>
        _held.TryGetValue((lockName, limit), out var held) && held >= limit;

    public void Enter(string lockName, int limit) => _held.AddOrUpdate((lockName, limit), 1, static (_, x) => x + 1);

    public void Exit(string lockName, int limit)
    {
        var key = (lockName, limit);
        while (_held.TryGetValue(key, out var held))
        {
            if (held <= 1)
            {
                // Removed only if still at the value we read, so a concurrent Enter is never lost.
                if (_held.TryRemove(new KeyValuePair<(string LockName, int Limit), int>(key, held)))
                {
                    return;
                }

                continue;
            }

            if (_held.TryUpdate(key, held - 1, held))
            {
                return;
            }
        }
    }

    internal int HeldCount(string lockName, int limit) => _held.TryGetValue((lockName, limit), out var held) ? held : 0;
}
