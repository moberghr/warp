using System.Text.Json;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.Policies;

namespace Warp.Core.RateLimit;

public class RateLimitPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IJobContext _jobContext;
    private readonly IWarpLockProvider _lockProvider;
    private readonly IRateLimitStore _store;
    private readonly RateLimitResolver _resolver;
    private readonly TimeProvider _timeProvider;
    private readonly RateLimitGate _gate;

    // Builds a behaviour with a private, instance-scoped RateLimitGate: correct, but it
    // remembers nothing across jobs and leases one slot at a time. The DI-registered shims share the
    // process-wide gate instead.
    public RateLimitPipelineBehavior(
        IJobContext jobContext,
        IWarpLockProvider lockProvider,
        IRateLimitStore store,
        RateLimitResolver resolver,
        TimeProvider timeProvider)
        : this(jobContext, lockProvider, store, resolver, timeProvider, new RateLimitGate(maxLeaseSize: 1))
    {
    }

    internal RateLimitPipelineBehavior(
        IJobContext jobContext,
        IWarpLockProvider lockProvider,
        IRateLimitStore store,
        RateLimitResolver resolver,
        TimeProvider timeProvider,
        RateLimitGate gate)
    {
        _jobContext = jobContext;
        _lockProvider = lockProvider;
        _store = store;
        _resolver = resolver;
        _timeProvider = timeProvider;
        _gate = gate;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        // Job-backed executions only. Saga proxies (and any other IPolicyExemptHandler) manage their own
        // execution policy — their busy/version-conflict reschedules must not also contend on a rate limit.
        if (PolicyResolver.Bypasses(request, _jobContext))
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<IRateLimitMetadata>();
        PolicyResolver.StampRateLimit(meta, _jobContext.HandlerType, typeof(TRequest));

        if (meta.RateLimitKey == null || meta.RateLimitCount == null || meta.RateLimitWindowSeconds == null)
        {
            return await next(request, cancellationToken);
        }

        var key = meta.RateLimitKey;
        var style = meta.RateLimitStyle ?? RateLimitStyle.Fixed;
        var mode = meta.RateLimitMode ?? RateLimitMode.Skip;
        var policy = new RateLimitPolicy(meta.RateLimitCount.Value, meta.RateLimitWindowSeconds.Value, style);

        using var span = WarpTelemetry.StartRateLimitActivity();
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitKey, key);

        // Known-exhausted key: refuse from memory — no override lookup, no lock, no bucket read. Sound
        // across servers because a recorded rejection is a lower bound on when the key reopens (see
        // RateLimitGate); a Wait-mode refusal needs a leased slot, and falls through when none is left.
        var cachedNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (_gate.TryRefuse(key, policy, mode, cachedNow, out var cached))
        {
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitCount, cached.EffectiveCount);
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitWindowSeconds, cached.EffectiveWindowSeconds);
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitStyle, style.ToString());
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitCached, true);
            Refuse(request, key, mode, cached.EffectiveCount, cached.EffectiveWindowSeconds, cached.ScheduleTo, cachedNow, span);

            return default!;
        }

        var ovr = await _resolver.GetOverride(key, cancellationToken);
        var effectiveCount = ovr?.Count ?? meta.RateLimitCount.Value;
        var effectiveWindow = TimeSpan.FromSeconds(ovr?.WindowSeconds ?? meta.RateLimitWindowSeconds.Value);

        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitCount, effectiveCount);
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitWindowSeconds, (int)effectiveWindow.TotalSeconds);
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitStyle, style.ToString());

        // The lock guards the check-and-increment ONLY. It must be released before the handler runs: held
        // across next(), every admitted job on the key ran one at a time and every other worker queued on
        // the lock for the full handler duration — the opposite of a rate limit (§8.8).
        var lockHandle = await _lockProvider.TryAcquireAsync(
            $"warp:ratelimit:{key}",
            TimeSpan.FromSeconds(5),
            cancellationToken);

        if (lockHandle == null)
        {
            // Defensive: lock contention beyond 5s. Reschedule with 100–500ms randomised
            // jitter (inclusive on both ends) so multiple workers hammering the same hot
            // key don't immediately thunder back into the lock together. Mirrors
            // CircuitBreakerPipelineBehavior's ResetJitter pattern.
            var contentionNow = _timeProvider.GetUtcNow().UtcDateTime;
            var jitterMs = Random.Shared.Next(100, 501);
            var rescheduleTo = contentionNow.AddMilliseconds(jitterMs);
            _jobContext.Outcome = new JobOutcome
            {
                State = JobOutcome.RescheduledState(rescheduleTo, contentionNow),
                ScheduleTime = rescheduleTo,

                // Routed message children keep HandlerType on requeue (§8.14) — it IS the routing decision.
                ClearHandlerType = request is not IMessage,
                Reason = OutcomeReason.RateLimit,
                LogMessage = $"Requeued — rate limit '{key}' lock contention",
            };
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeLockContention);

            return default!;
        }

        bool allowed;
        await using (lockHandle)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var evaluation = await Evaluate(key, effectiveCount, effectiveWindow, style, now, cancellationToken);
            allowed = evaluation.Allowed;

            if (!allowed)
            {
                var windowSeconds = (int)effectiveWindow.TotalSeconds;
                var scheduleTo = evaluation.NextAvailable;
                if (mode == RateLimitMode.Wait)
                {
                    scheduleTo = await AssignSlotAsync(key, policy, effectiveCount, effectiveWindow, style, evaluation, now, cancellationToken);
                }
                else
                {
                    _gate.RecordRejection(key, policy, effectiveCount, windowSeconds, now, evaluation.NextAvailable, []);
                }

                Refuse(request, key, mode, effectiveCount, windowSeconds, scheduleTo, now, span);
            }
        }

        if (!allowed)
        {
            return default!;
        }

        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeAcquired);

        return await next(request, cancellationToken);
    }

    private void Refuse(
        TRequest request,
        string key,
        RateLimitMode mode,
        int effectiveCount,
        int windowSeconds,
        DateTime scheduleTo,
        DateTime now,
        System.Diagnostics.Activity? span)
    {
        if (mode == RateLimitMode.Wait)
        {
            _jobContext.Outcome = new JobOutcome
            {
                State = JobOutcome.RescheduledState(scheduleTo, now),
                ScheduleTime = scheduleTo,

                // Routed message children keep HandlerType on requeue (§8.14) — it IS the routing decision.
                ClearHandlerType = request is not IMessage,
                Reason = OutcomeReason.RateLimit,
                LogMessage = $"Throttled — rate limit '{key}' ({effectiveCount}/{windowSeconds}s), rescheduled to {scheduleTo:O}",
            };
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeThrottled);

            return;
        }

        _jobContext.Outcome = new JobOutcome
        {
            State = State.Deleted,
            Reason = OutcomeReason.RateLimit,
            LogMessage = $"Cancelled — rate limit '{key}' exceeded ({effectiveCount}/{windowSeconds}s)",
        };
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeSkipped);
    }

    /// <summary>
    /// Picks the instant a throttled Wait-mode job is rescheduled to. Every job refused in one window used
    /// to be rescheduled onto the same instant — the key's reopening — so the whole backlog woke together,
    /// <c>count</c> of it was admitted and the rest re-executed only to be refused again, every window:
    /// quadratic in the backlog. Instead each job takes its own slot from the cluster-wide cursor on the
    /// bucket row, one position per <c>window / count</c>, so a throttled job comes back roughly when there
    /// is capacity for it. Runs under the key's lock, so the cursor read in the evaluation is still current.
    /// <para>
    /// A <see cref="RateLimitStyle.Fixed"/> window admits its whole <c>count</c> at once, so its jobs are
    /// scheduled at the START of the window their position falls in: exactly <c>count</c> per window, at
    /// the earliest instant it allows. Spreading them across the window instead would delay the last of a
    /// backlog by up to a whole window for nothing. Sliding and TokenBucket return capacity gradually, so
    /// their positions are used as-is.
    /// </para>
    /// </summary>
    private async Task<DateTime> AssignSlotAsync(
        string key,
        RateLimitPolicy policy,
        int count,
        TimeSpan window,
        RateLimitStyle style,
        RateLimitEvaluation evaluation,
        DateTime now,
        CancellationToken ct)
    {
        var windowSeconds = (int)window.TotalSeconds;

        // A slot this process leased earlier and has not used is still a reservation on the cursor.
        if (_gate.TryTakeLeasedSlot(key, evaluation.NextAvailable, out var leased))
        {
            _gate.RecordRejection(key, policy, count, windowSeconds, now, evaluation.NextAvailable, []);

            return leased;
        }

        // A window shorter than one activation tick: slots closer together than the tick are released
        // together anyway, and a released batch is consumed in well under a second, so it only ever meets
        // the window open at that moment and only `count` of it can get in. So a short window takes one
        // window's worth per activation tick and nothing more. Measured on 50 per 2 s (500 jobs, 10 s ticks):
        // ~0.9 refused runs per job and 62 statements per job, against 2.4 and 90 for count x tick / window
        // per step, which drained faster but re-ran most of each batch. Lowering ScheduledActivationInterval
        // toward the window restores the full rate.
        var shortWindow = window < _gate.ActivationInterval;
        var step = shortWindow ? _gate.ActivationInterval : window;
        var perStep = Math.Max(count, 1);
        var spacing = TimeSpan.FromTicks(Math.Max(1, step.Ticks / perStep));

        // Fixed windows, and every short-window step, bring their jobs back together at the start of the step.
        // Sliding and TokenBucket on a full-length window keep their evenly spaced positions.
        var bucket = shortWindow || style == RateLimitStyle.Fixed ? step : TimeSpan.Zero;
        var first = evaluation.NextSlot is { } cursor && cursor > evaluation.NextAvailable ? cursor : evaluation.NextAvailable;
        var size = _gate.LeaseSize(perStep, spacing);

        if (!await _store.SetNextSlotAsync(key, first + (spacing * size), ct))
        {
            // No bucket row to hold the cursor — unreachable in practice (a refusal implies an earlier
            // accept wrote the row). Fall back to the unspread reopening instant rather than fail the job.
            _gate.RecordRejection(key, policy, count, windowSeconds, now, evaluation.NextAvailable, []);

            return evaluation.NextAvailable;
        }

        var spare = Enumerable.Range(1, size - 1)
            .Select(x => ToScheduleTime(first + (spacing * x), bucket, evaluation.NextAvailable))
            .ToList();
        _gate.RecordRejection(key, policy, count, windowSeconds, now, evaluation.NextAvailable, spare);

        return ToScheduleTime(first, bucket, evaluation.NextAvailable);
    }

    // The start of the bucket the cursor position falls in, anchored on the reopening instant so the mapping
    // holds however the window is aligned. A zero bucket keeps the position as-is.
    private static DateTime ToScheduleTime(DateTime position, TimeSpan bucket, DateTime reopening)
    {
        if (bucket <= TimeSpan.Zero || position <= reopening)
        {
            return position;
        }

        var bucketsAhead = (position - reopening).Ticks / bucket.Ticks;

        return reopening.AddTicks(bucketsAhead * bucket.Ticks);
    }

    private async Task<RateLimitEvaluation> Evaluate(
        string key,
        int count,
        TimeSpan window,
        RateLimitStyle style,
        DateTime now,
        CancellationToken ct)
    {
        var bucket = await _store.GetAsync(key, ct);

        var evaluation = style switch
        {
            RateLimitStyle.Sliding => await EvaluateSliding(key, count, window, now, bucket, ct),
            RateLimitStyle.TokenBucket => await EvaluateTokenBucket(key, count, window, now, bucket, ct),
            _ => await EvaluateFixed(key, count, window, now, bucket, ct),
        };

        return evaluation with { NextSlot = bucket?.NextSlotUtc };
    }

    private async Task<RateLimitEvaluation> EvaluateTokenBucket(
        string key,
        int count,
        TimeSpan window,
        DateTime now,
        RateLimitBucket? bucket,
        CancellationToken ct)
    {
        // Token bucket: burst capacity = count, refill rate = count / window per second. A fresh key
        // starts full (allows an initial burst up to capacity). Tokens accrue continuously, but only an
        // ACCEPT changes stored state — so between accepts the available tokens are derived from the time
        // elapsed since the last accept (WindowStartUtc = last-refill instant, TimestampsJson = the
        // fractional token count at that instant). Rejections write nothing, mirroring Fixed/Sliding.
        var capacity = (double)count;

        // Degenerate window (only reachable via a hand-constructed IRateLimitMetadata; all public entry
        // points clamp perSeconds >= 1) — don't divide by zero / produce NaN schedule times; just allow.
        if (window.TotalSeconds <= 0)
        {
            return new RateLimitEvaluation(true, default);
        }

        var ratePerSecond = capacity / window.TotalSeconds;

        double available;
        if (bucket == null)
        {
            available = capacity;
        }
        else
        {
            var stored = ParseTokens(bucket.TimestampsJson);
            var elapsedSeconds = (now - bucket.WindowStartUtc).TotalSeconds;
            var accrued = elapsedSeconds > 0 ? elapsedSeconds * ratePerSecond : 0;
            available = Math.Min(capacity, stored + accrued);
        }

        if (available >= 1.0)
        {
            var remaining = available - 1.0;
            await _store.UpsertAsync(key, now, (int)remaining, SerializeTokens(remaining), now, ct);

            return new RateLimitEvaluation(true, default);
        }

        // Not enough for one token — pace: reschedule to when the deficit will have refilled.
        var secondsToNextToken = (1.0 - available) / ratePerSecond;

        return new RateLimitEvaluation(false, now.AddSeconds(secondsToNextToken));
    }

    private async Task<RateLimitEvaluation> EvaluateFixed(
        string key,
        int count,
        TimeSpan window,
        DateTime now,
        RateLimitBucket? bucket,
        CancellationToken ct)
    {
        var windowStart = bucket?.WindowStartUtc ?? DateTime.MinValue;
        var currentCount = bucket?.CurrentCount ?? 0;

        // Windows are floor-aligned to global UTC tick boundaries, not to "first use of this
        // key". Two keys with the same window length therefore roll over at identical wall-clock
        // moments — predictable for reasoning, with the well-known caveat that the first window
        // after key creation can be shorter than `window`.
        if (bucket == null || now >= windowStart + window)
        {
            var ticks = window.Ticks;
            var floored = (now.Ticks / ticks) * ticks;
            windowStart = new DateTime(floored, DateTimeKind.Utc);
            currentCount = 0;
        }

        if (currentCount < count)
        {
            currentCount++;
            await _store.UpsertAsync(key, windowStart, currentCount, null, now, ct);

            return new RateLimitEvaluation(true, default);
        }

        return new RateLimitEvaluation(false, windowStart + window);
    }

    private async Task<RateLimitEvaluation> EvaluateSliding(
        string key,
        int count,
        TimeSpan window,
        DateTime now,
        RateLimitBucket? bucket,
        CancellationToken ct)
    {
        var threshold = now - window;
        var existing = ParseTimestamps(bucket?.TimestampsJson);
        var pruned = existing
            .Where(t => t > threshold)
            .Order()
            .ToList();

        // Defensive: if a previous bug or hand-edit left more than `count` entries in the
        // bucket, keep only the most-recent `count` in the in-memory list used for the
        // accept/reject decision and the nextAvailable calculation. The bloated row itself
        // is NOT rewritten here — that would add a DB write on every rejection of a corrupted
        // key. Natural decay (entries falling out of the window) self-heals the stored state
        // over time; this trim just ensures we don't compound the overflow on future reads.
        if (pruned.Count > count)
        {
            pruned = [.. pruned.TakeLast(count)];
        }

        if (pruned.Count < count)
        {
            pruned.Add(now);
            var json = SerializeTimestamps(pruned);
            await _store.UpsertAsync(key, default, pruned.Count, json, now, ct);

            return new RateLimitEvaluation(true, default);
        }

        return new RateLimitEvaluation(false, pruned[0] + window);
    }

    private static List<DateTime> ParseTimestamps(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        // Defensive: the bucket row is keyed by name only, so a key reused across styles (or a hand-edit)
        // could leave a non-array payload here (e.g. a token-bucket scalar). Treat anything unparseable as
        // an empty window rather than throwing out of the pipeline and failing the job.
        try
        {
            var ticks = JsonSerializer.Deserialize<long[]>(json) ?? [];

            return [.. ticks.Select(t => new DateTime(t, DateTimeKind.Utc))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeTimestamps(List<DateTime> timestamps)
    {
        long[] ticks = [.. timestamps.Select(t => t.Ticks)];

        return JsonSerializer.Serialize(ticks);
    }

    // Token bucket stores a single fractional token count in the same TimestampsJson column the sliding
    // window uses for its tick array — the two styles never share a bucket row, so the column is free.
    private static double ParseTokens(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return 0;
        }

        // Defensive: same shared-row concern as ParseTimestamps — a sliding-window tick array left on the
        // row would not deserialize to a double. Treat an unparseable payload as an empty bucket.
        try
        {
            return JsonSerializer.Deserialize<double>(json);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string SerializeTokens(double tokens) => JsonSerializer.Serialize(tokens);

    private readonly record struct RateLimitEvaluation(bool Allowed, DateTime NextAvailable, DateTime? NextSlot = null);
}

/// <summary>
/// DI shim: carries the <c>IJob</c> constraint so an in-memory <c>Send</c> never resolves
/// <see cref="IRateLimitStore"/> and its scoped DbContext. See the retry shims.
/// </summary>
internal sealed class RateLimitJobPipelineBehavior<TRequest, TResponse> : RateLimitPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IJob
{
    public RateLimitJobPipelineBehavior(
        IJobContext jobContext,
        IWarpLockProvider lockProvider,
        IRateLimitStore store,
        RateLimitResolver resolver,
        TimeProvider timeProvider,
        RateLimitGate gate)
        : base(jobContext, lockProvider, store, resolver, timeProvider, gate)
    {
    }
}

/// <summary>DI shim: the <c>IMessage</c> half of the constraint split — see <see cref="RateLimitJobPipelineBehavior{TRequest, TResponse}"/>.</summary>
internal sealed class RateLimitMessagePipelineBehavior<TRequest, TResponse> : RateLimitPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IMessage
{
    public RateLimitMessagePipelineBehavior(
        IJobContext jobContext,
        IWarpLockProvider lockProvider,
        IRateLimitStore store,
        RateLimitResolver resolver,
        TimeProvider timeProvider,
        RateLimitGate gate)
        : base(jobContext, lockProvider, store, resolver, timeProvider, gate)
    {
    }
}
