using Warp.Core.Data.Entities;

namespace Warp.Core.RateLimit;

public interface IRateLimitStore
{
    Task<RateLimitBucket?> GetAsync(string name, CancellationToken ct);

    Task UpsertAsync(string name, DateTime windowStartUtc, int currentCount, string? timestampsJson, DateTime updatedAt, CancellationToken ct);

    /// <summary>
    /// Advances the key's Wait-mode slot cursor (<see cref="RateLimitBucket.NextSlotUtc"/>). Called under
    /// the key's lock, after a rejection. Returns false when the cursor could not be stored — the bucket
    /// row does not exist, or the store does not support one — and the throttled job is then rescheduled
    /// to the key's reopening instant, unspread, as before the cursor existed.
    /// </summary>
    /// <remarks>
    /// Default-implemented so a custom store written against the earlier interface keeps compiling and
    /// keeps working, without backlog spreading.
    /// </remarks>
    Task<bool> SetNextSlotAsync(string name, DateTime nextSlotUtc, CancellationToken ct) => Task.FromResult(false);
}
