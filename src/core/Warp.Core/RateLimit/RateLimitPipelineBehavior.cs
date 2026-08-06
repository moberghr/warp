using System.Text.Json;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;

namespace Warp.Core.RateLimit;

public class RateLimitPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IJobContext _jobContext;
    private readonly IWarpLockProvider _lockProvider;
    private readonly IRateLimitStore _store;
    private readonly RateLimitResolver _resolver;
    private readonly TimeProvider _timeProvider;

    public RateLimitPipelineBehavior(
        IJobContext jobContext,
        IWarpLockProvider lockProvider,
        IRateLimitStore store,
        RateLimitResolver resolver,
        TimeProvider timeProvider)
    {
        _jobContext = jobContext;
        _lockProvider = lockProvider;
        _store = store;
        _resolver = resolver;
        _timeProvider = timeProvider;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IJob)
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<IRateLimitMetadata>();
        if (meta.RateLimitKey == null || meta.RateLimitCount == null || meta.RateLimitWindowSeconds == null)
        {
            return await next(request, cancellationToken);
        }

        var key = meta.RateLimitKey;
        var ovr = await _resolver.GetOverride(key, cancellationToken);
        var effectiveCount = ovr?.Count ?? meta.RateLimitCount.Value;
        var effectiveWindow = TimeSpan.FromSeconds(ovr?.WindowSeconds ?? meta.RateLimitWindowSeconds.Value);
        var style = meta.RateLimitStyle ?? RateLimitStyle.Fixed;
        var mode = meta.RateLimitMode ?? RateLimitMode.Skip;

        using var span = WarpTelemetry.StartRateLimitActivity();
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitKey, key);
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitCount, effectiveCount);
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitWindowSeconds, (int)effectiveWindow.TotalSeconds);
        span?.SetTag(WarpTelemetryAttributes.WarpRateLimitStyle, style.ToString());

        await using var lockHandle = await _lockProvider.TryAcquireAsync(
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
                ClearHandlerType = true,
                Reason = OutcomeReason.RateLimit,
                LogMessage = $"Requeued — rate limit '{key}' lock contention",
            };
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeLockContention);

            return default!;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var evaluation = await Evaluate(key, effectiveCount, effectiveWindow, style, now, cancellationToken);

        if (evaluation.Allowed)
        {
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeAcquired);

            return await next(request, cancellationToken);
        }

        var windowSeconds = (int)effectiveWindow.TotalSeconds;
        if (mode == RateLimitMode.Wait)
        {
            _jobContext.Outcome = new JobOutcome
            {
                State = JobOutcome.RescheduledState(evaluation.NextAvailable, now),
                ScheduleTime = evaluation.NextAvailable,
                ClearHandlerType = true,
                Reason = OutcomeReason.RateLimit,
                LogMessage = $"Throttled — rate limit '{key}' ({effectiveCount}/{windowSeconds}s), rescheduled to {evaluation.NextAvailable:O}",
            };
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeThrottled);
        }
        else
        {
            _jobContext.Outcome = new JobOutcome
            {
                State = State.Deleted,
                Reason = OutcomeReason.RateLimit,
                LogMessage = $"Cancelled — rate limit '{key}' exceeded ({effectiveCount}/{windowSeconds}s)",
            };
            span?.SetTag(WarpTelemetryAttributes.WarpRateLimitOutcome, WarpTelemetryAttributes.WarpRateLimitOutcomeSkipped);
        }

        return default!;
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

        return style switch
        {
            RateLimitStyle.Sliding => await EvaluateSliding(key, count, window, now, bucket, ct),
            RateLimitStyle.TokenBucket => await EvaluateTokenBucket(key, count, window, now, bucket, ct),
            _ => await EvaluateFixed(key, count, window, now, bucket, ct),
        };
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

    private readonly record struct RateLimitEvaluation(bool Allowed, DateTime NextAvailable);
}
