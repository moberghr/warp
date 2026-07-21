using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Core.Adapters;

/// <summary>
/// Cluster-shared, DB-backed token-leasing rate limiter for an adapter. Each process leases a chunk of
/// tokens (<c>max(1, limit/10)</c>) in one row-locked check-and-increment on the shared
/// <c>RateLimitBucket</c> row (key <c>warp:adapter:{name}</c>, disjoint namespace §8.6) and spends them
/// locally, returning to the DB only when its lease is empty — no per-attempt round-trip. A crash loses
/// only unspent lease tokens (under-admission, the safe direction). Runs on its own DI scope created via
/// <see cref="IServiceScopeFactory"/> (§0.5) — deliberately <b>not</b> the handler-scope commit semantics
/// of <c>RateLimitStore</c>.
/// </summary>
public interface IAdapterRateLimiter
{
    /// <summary>
    /// Acquires one token for <paramref name="adapter"/> before a physical attempt, honouring the shared
    /// cluster budget. <paramref name="limit"/> / <paramref name="perSeconds"/> are the process's local
    /// policy; the effective policy is reconciled against the persisted <c>AdapterDefinition</c> and any
    /// admin <c>RateLimitOverride</c> (precedence override &gt; persisted &gt; local). On overflow the
    /// behaviour follows <paramref name="overflow"/>: <see cref="AdapterRateLimitOverflow.Wait"/> delays up
    /// to <paramref name="maxWait"/> for the next window then throws; <see cref="AdapterRateLimitOverflow.FailFast"/>
    /// throws immediately. Both throw <see cref="AdapterRateLimitedException"/>.
    /// </summary>
    Task AcquireAsync(string adapter, int limit, int perSeconds, AdapterRateLimitOverflow overflow, TimeSpan maxWait, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IAdapterRateLimiter"/> keyed on the user's <typeparamref name="TContext"/>. Singleton
/// so the per-adapter local lease state persists across calls. Registered by <c>AddAdapters()</c>; resolves
/// <typeparamref name="TContext"/> + <see cref="IWarpSqlQueries{TContext}"/> +
/// <see cref="IDatabaseExceptionClassifier"/> per lease acquisition from a fresh scope.
/// </summary>
internal sealed class AdapterRateLimiter<TContext> : IAdapterRateLimiter
    where TContext : DbContext
{
    private const int MaxInsertRetries = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdapterRateLimiter<TContext>> _logger;

    // Ordinal (case-SENSITIVE) so in-memory adapter identity matches the case-sensitive DB row / counter
    // keys — "Stripe" and "stripe" are two independent adapters everywhere, never one in memory and two in
    // the DB (which would split rate-limit budgets). See AdapterName.Validate.
    private readonly ConcurrentDictionary<string, AdapterLeaseState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _conflictWarned = new(StringComparer.Ordinal);

    public AdapterRateLimiter(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<AdapterRateLimiter<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Test-only gate awaited at the start of each DB lease acquisition (before the transaction opens) so
    /// tests can pin two "processes" concurrently on the contended row with <c>BarrierSignal</c> (§4.7).
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeLeaseAcquire { get; set; }

    public async Task AcquireAsync(string adapter, int limit, int perSeconds, AdapterRateLimitOverflow overflow, TimeSpan maxWait, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);

        var startedAt = _timeProvider.GetTimestamp();

        while (true)
        {
            var attempt = await TryAcquireTokenAsync(adapter, limit, perSeconds, ct);
            if (attempt.Acquired)
            {
                return;
            }

            if (overflow == AdapterRateLimitOverflow.FailFast)
            {
                throw new AdapterRateLimitedException(
                    $"Adapter '{adapter}' shared rate limit exceeded ({limit}/{perSeconds}s); failing fast.");
            }

            var elapsed = _timeProvider.GetElapsedTime(startedAt);
            if (elapsed >= maxWait)
            {
                throw new AdapterRateLimitedException(
                    $"Adapter '{adapter}' shared rate limit wait exceeded {maxWait.TotalMilliseconds:F0}ms.");
            }

            var delay = attempt.RetryAfter <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(25) : attempt.RetryAfter;
            var budget = maxWait - elapsed;
            if (delay > budget)
            {
                delay = budget;
            }

            await Task.Delay(delay, _timeProvider, ct);
        }
    }

    private async Task<TokenAttempt> TryAcquireTokenAsync(string adapter, int limit, int perSeconds, CancellationToken ct)
    {
        var state = _states.GetOrAdd(adapter, _ => new AdapterLeaseState());
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Fast path: spend a local lease token that is still inside its leased window.
        lock (state.Gate)
        {
            if (state.Remaining > 0 && now < state.WindowEnd)
            {
                state.Remaining--;

                return TokenAttempt.Ok();
            }
        }

        // Slow path: lease a fresh chunk from the DB. Serialise per adapter so two concurrent callers in
        // this process do not each open a lease when one chunk would cover both.
        await state.DbGate.WaitAsync(ct);
        try
        {
            now = _timeProvider.GetUtcNow().UtcDateTime;

            lock (state.Gate)
            {
                if (state.Remaining > 0 && now < state.WindowEnd)
                {
                    state.Remaining--;

                    return TokenAttempt.Ok();
                }
            }

            var lease = await AcquireLeaseFromDbAsync(adapter, limit, perSeconds, now, ct);
            if (lease.Granted <= 0)
            {
                return TokenAttempt.Denied(lease.RetryAfter);
            }

            lock (state.Gate)
            {
                state.WindowEnd = lease.WindowEnd;
                state.Remaining = lease.Granted - 1; // spend one now, bank the rest for this window
            }

            return TokenAttempt.Ok();
        }
        finally
        {
            state.DbGate.Release();
        }
    }

    private async Task<LeaseResult> AcquireLeaseFromDbAsync(string adapter, int limit, int perSeconds, DateTime now, CancellationToken ct)
    {
        if (BeforeLeaseAcquire is not null)
        {
            await BeforeLeaseAcquire(ct);
        }

        var key = AdapterSharedPolicy.BucketKey(adapter);
        var retry = 0;

        while (true)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
            var classifier = scope.ServiceProvider.GetRequiredService<IDatabaseExceptionClassifier>();

            await using var tx = await context.Database.BeginTransactionAsync(ct);

            var bucket = await queries.LockRateLimitBucketByKeyAsync(context, key, ct);
            var effective = await ResolveEffectivePolicyAsync(context, adapter, key, limit, perSeconds, now, ct);

            var windowStart = FloorWindow(now, effective.PerSeconds);
            var windowEnd = windowStart.AddSeconds(effective.PerSeconds);
            var currentCount = bucket is not null && bucket.WindowStartUtc == windowStart ? bucket.CurrentCount : 0;
            var remaining = effective.Limit - currentCount;

            if (remaining <= 0)
            {
                // No tokens this window — still commit any definition reconciliation, then report retry timing.
                await context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return LeaseResult.None(windowEnd - now);
            }

            var grant = Math.Min(Math.Max(1, effective.Limit / 10), remaining);
            var newCount = currentCount + grant;

            if (bucket is null)
            {
                context.Set<RateLimitBucket>().Add(new RateLimitBucket
                {
                    Name = key,
                    WindowStartUtc = windowStart,
                    CurrentCount = newCount,
                    UpdatedAt = now,
                });
            }
            else
            {
                bucket.WindowStartUtc = windowStart;
                bucket.CurrentCount = newCount;
                bucket.UpdatedAt = now;
            }

            try
            {
                await context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return LeaseResult.Lease(grant, windowEnd);
            }
            catch (DbUpdateException ex) when (retry < MaxInsertRetries
                && (classifier.IsUniqueConstraintViolation(ex) || classifier.IsTransientDeadlock(ex)))
            {
                // Another process won the race to insert the bucket or definition row, or a transient insert
                // deadlock occurred. Roll back and retry: the row now exists so the next pass row-locks it.
                await tx.RollbackAsync(ct);
                retry++;
            }
        }
    }

    private async Task<EffectivePolicy> ResolveEffectivePolicyAsync(
        DbContext context,
        string adapter,
        string key,
        int localLimit,
        int localPerSeconds,
        DateTime now,
        CancellationToken ct)
    {
        var localHash = AdapterSharedPolicy.Hash(localLimit, localPerSeconds);

        var definition = await context.Set<AdapterDefinition>()
            .Where(x => x.Name == adapter)
            .FirstOrDefaultAsync(ct);

        if (definition is null)
        {
            // First writer registers its local policy (first-writer-wins persistence; converges once a row exists).
            context.Set<AdapterDefinition>().Add(new AdapterDefinition
            {
                Name = adapter,
                FirstSeenAt = now,
                LastSeenAt = now,
                SharedPolicyJson = AdapterSharedPolicy.ToJson(localLimit, localPerSeconds),
                SharedPolicyHash = localHash,
                HasPolicyConflict = false,
            });
        }
        else if (definition.SharedPolicyHash is null)
        {
            definition.SharedPolicyJson = AdapterSharedPolicy.ToJson(localLimit, localPerSeconds);
            definition.SharedPolicyHash = localHash;
            definition.HasPolicyConflict = false;
        }
        else if (!string.Equals(definition.SharedPolicyHash, localHash, StringComparison.Ordinal))
        {
            // Persisted differs from this process's local policy — enforce the persisted one (deterministic
            // cluster behaviour even mid-rolling-deploy) and flag the conflict for the dashboard badge.
            if (!definition.HasPolicyConflict)
            {
                definition.HasPolicyConflict = true;
            }

            WarnConflictOnce(adapter);
            WarpTelemetry.AdapterConfigConflicts.Add(
                1,
                new KeyValuePair<string, object?>(WarpTelemetryAttributes.AdapterMeterAdapter, adapter));
        }
        else if (definition.HasPolicyConflict)
        {
            // Local hash now matches persisted again (the conflicting deploy was rolled back or realigned).
            // The entity doc promises the flag clears on a matching re-registration — clear it, and only
            // when it is currently set so a matching process does not dirty the row on every lease.
            definition.HasPolicyConflict = false;
        }

        // Admin override wins outright over both persisted and local.
        var overrideRow = await context.Set<RateLimitOverride>()
            .AsNoTracking()
            .Where(x => x.Name == key)
            .FirstOrDefaultAsync(ct);

        if (overrideRow is not null)
        {
            return new EffectivePolicy(overrideRow.Count, overrideRow.WindowSeconds);
        }

        var persisted = definition?.SharedPolicyHash is not null && definition.SharedPolicyJson is not null
            ? AdapterSharedPolicy.Parse(definition.SharedPolicyJson)
            : null;

        if (persisted is not null && !string.Equals(definition!.SharedPolicyHash, localHash, StringComparison.Ordinal))
        {
            return new EffectivePolicy(persisted.Limit, persisted.PerSeconds);
        }

        return new EffectivePolicy(localLimit, localPerSeconds);
    }

    private void WarnConflictOnce(string adapter)
    {
        if (_conflictWarned.TryAdd(adapter, 0))
        {
            _logger.LogWarning(
                "Adapter {Adapter} local shared rate-limit policy differs from the persisted cluster policy; enforcing the persisted policy.",
                adapter);
        }
    }

    private static DateTime FloorWindow(DateTime nowUtc, int perSeconds)
    {
        // Guard against a non-positive window (only reachable via a hand-written RateLimitOverride row with
        // WindowSeconds <= 0): a 0 would divide-by-zero here and throw on every rate-limited attempt. Clamp
        // to a 1-second window rather than crash the call.
        var windowSeconds = perSeconds < 1 ? 1 : perSeconds;
        var windowTicks = windowSeconds * TimeSpan.TicksPerSecond;

        return new DateTime(nowUtc.Ticks / windowTicks * windowTicks, DateTimeKind.Utc);
    }

    private sealed class AdapterLeaseState
    {
        public object Gate { get; } = new();

        public SemaphoreSlim DbGate { get; } = new(1, 1);

        public int Remaining { get; set; }

        public DateTime WindowEnd { get; set; }
    }

    private readonly struct TokenAttempt
    {
        private TokenAttempt(bool acquired, TimeSpan retryAfter)
        {
            Acquired = acquired;
            RetryAfter = retryAfter;
        }

        public bool Acquired { get; }

        public TimeSpan RetryAfter { get; }

        public static TokenAttempt Ok() => new(true, TimeSpan.Zero);

        public static TokenAttempt Denied(TimeSpan retryAfter) => new(false, retryAfter);
    }

    private readonly struct LeaseResult
    {
        private LeaseResult(int granted, DateTime windowEnd, TimeSpan retryAfter)
        {
            Granted = granted;
            WindowEnd = windowEnd;
            RetryAfter = retryAfter;
        }

        public int Granted { get; }

        public DateTime WindowEnd { get; }

        public TimeSpan RetryAfter { get; }

        public static LeaseResult Lease(int granted, DateTime windowEnd) => new(granted, windowEnd, TimeSpan.Zero);

        public static LeaseResult None(TimeSpan retryAfter) => new(0, default, retryAfter);
    }

    private sealed record EffectivePolicy(int Limit, int PerSeconds);
}

/// <summary>
/// Shared-policy serialisation + hashing for the adapter rate limiter. The persisted policy carries only
/// the coordinated numbers (<c>limit</c> + <c>perSeconds</c>); overflow/maxWait are local reactions and are
/// deliberately excluded from the hash so they may differ per process without registering as a conflict.
/// </summary>
internal static class AdapterSharedPolicy
{
    public static string BucketKey(string adapter) => $"warp:adapter:{adapter}";

    public static string ToJson(int limit, int perSeconds) => JsonSerializer.Serialize(new SharedPolicyDoc(limit, perSeconds));

    public static SharedPolicyDoc? Parse(string json) => JsonSerializer.Deserialize<SharedPolicyDoc>(json);

    public static string Hash(int limit, int perSeconds)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson(limit, perSeconds))));
}

/// <summary>Persisted shared rate-limit policy shape stored in <c>AdapterDefinition.SharedPolicyJson</c>.</summary>
internal sealed record SharedPolicyDoc(int Limit, int PerSeconds);
