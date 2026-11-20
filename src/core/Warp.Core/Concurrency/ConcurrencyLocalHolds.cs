using System.Collections.Concurrent;

namespace Warp.Core.Concurrency;

/// <summary>
/// How many slots of each concurrency key THIS process currently holds.
/// <para>
/// Every hold counted here is a real database hold, so when this process alone already holds
/// <c>limit</c> slots of a key, the database acquire is certain to fail — the refusal can be decided
/// without the round trip (and without opening a lock-pool connection, §2.18). The answer is exact, not
/// a heuristic: it can only say "full" when the key IS full. A hold taken by another process is invisible
/// here, so those refusals still go to the database exactly as before.
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
    private readonly ConcurrentDictionary<string, int> _held = new(StringComparer.Ordinal);

    public bool IsSaturated(string lockName, int limit) =>
        _held.TryGetValue(lockName, out var held) && held >= limit;

    public void Enter(string lockName) => _held.AddOrUpdate(lockName, 1, static (_, x) => x + 1);

    public void Exit(string lockName)
    {
        while (_held.TryGetValue(lockName, out var held))
        {
            if (held <= 1)
            {
                // Removed only if still at the value we read, so a concurrent Enter is never lost.
                if (_held.TryRemove(new KeyValuePair<string, int>(lockName, held)))
                {
                    return;
                }

                continue;
            }

            if (_held.TryUpdate(lockName, held - 1, held))
            {
                return;
            }
        }
    }

    internal int HeldCount(string lockName) => _held.TryGetValue(lockName, out var held) ? held : 0;
}
