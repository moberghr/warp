using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Notifiers;

namespace Warp.Worker.Services;

/// <summary>
/// Drains the write-optimized <see cref="ErrorOccurrence"/> inbox and folds it into durable <see cref="ErrorGroup"/>
/// issues off the worker hot path (§8.29) — the error-signal analogue of <see cref="CounterAggregator{TContext}"/>.
/// Computes the fingerprint here (never on the worker), upserts the group (count / first-last-seen / sample),
/// writes the hourly trend Counter, guards cardinality with a per-source <c>{other}</c> bucket, and detects
/// regressions. Drain-and-delete is exactly-once by construction (no cursor). A regression fires
/// <see cref="IssueRegressedEvent"/> from <see cref="OnCommittedAsync"/> so it's dispatched post-commit (§8.25).
/// </summary>
public sealed class ErrorGroupAggregator<TContext> : IServerTask
    where TContext : DbContext
{
    private const int DrainBatchSize = 1000;

    private const string OtherToken = "{other}";

    private const int MaxSamples = 10;

    private const int SampleMessageMax = 300;

    private const int MaxSamplesJsonLength = 4000;

    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly WarpNotifierDispatcher _notifierDispatcher;
    private readonly ILogger<ErrorGroupAggregator<TContext>> _logger;
    private readonly List<IssueRegressedEvent> _pendingRegressions = [];
    private readonly HashSet<ErrorSource> _cardinalityCappedWarned = [];

    public ErrorGroupAggregator(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider,
        WarpNotifierDispatcher notifierDispatcher,
        ILogger<ErrorGroupAggregator<TContext>> logger)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _notifierDispatcher = notifierDispatcher;
        _logger = logger;
    }

    public string Name => "AggregateErrorGroups";

    public string? LockKey => "warp:error-grouping";

    public TimeSpan? DefaultInterval => _configuration.ErrorGroupingInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        _pendingRegressions.Clear();
        _cardinalityCappedWarned.Clear();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Per-source distinct-group counts for the cardinality guard — loaded once; incremented as we create.
        var sourceCounts = await _context.Set<ErrorGroup>()
            .GroupBy(x => x.Source)
            .Select(x => new { Source = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Source, x => x.Count, ct);

        var total = 0;
        while (true)
        {
            var batch = await _context.Set<ErrorOccurrence>()
                .OrderBy(x => x.Timestamp)
                .Take(DrainBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            await FoldBatchAsync(batch, sourceCounts, now, ct);

            _context.Set<ErrorOccurrence>().RemoveRange(batch);
            await _context.SaveChangesAsync(ct);

            total += batch.Count;
        }

        return total > 0 ? $"Grouped {total} error occurrence(s)" : null;
    }

    public async Task OnCommittedAsync(CancellationToken ct)
    {
        foreach (var evt in _pendingRegressions)
        {
            await _notifierDispatcher.DispatchAsync(evt, CancellationToken.None);
        }
    }

    private async Task FoldBatchAsync(List<ErrorOccurrence> batch, Dictionary<ErrorSource, int> sourceCounts, DateTime now, CancellationToken ct)
    {
        var resolved = batch.ConvertAll(Resolve);

        // Load every group this batch might touch — the real fingerprints AND the per-source {other} buckets.
        var fingerprints = resolved.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);
        foreach (var source in resolved.Select(x => x.Occurrence.Source).Distinct())
        {
            fingerprints.Add(OtherFingerprint(source));
        }

        var wanted = fingerprints.ToList();
        var groups = (await _context.Set<ErrorGroup>()
                .Where(x => wanted.Contains(x.Fingerprint))
                .ToListAsync(ct))
            .ToDictionary(x => x.Fingerprint, StringComparer.Ordinal);

        foreach (var perFingerprint in resolved.GroupBy(x => x.Fingerprint, StringComparer.Ordinal))
        {
            var items = perFingerprint.ToList();
            var sample = items.MaxBy(x => x.Occurrence.Timestamp)!;

            var effective = ResolveCardinality(items[0], groups, sourceCounts);
            UpsertGroup(effective, items, groups, sourceCounts, now);

            // Hourly trend Counter (survives raw-row and even ErrorGroup cleanup, §8.22).
            var hour = items.Max(x => x.Occurrence.Timestamp);
            _context.Set<Counter>().Add(new Counter { Key = ErrorGroupKeys.HourlyKey(effective.Fingerprint, hour), Value = items.Count });

            // Always-on occurrence meter (§8.29) so the trend renders from an external TSDB. Fingerprint is
            // already the cardinality-collapsed effective fingerprint (bounded by the distinct-group cap).
            Warp.Core.Logging.WarpTelemetry.RecordErrorGroupOccurrence(effective.Fingerprint, sample.Occurrence.Application, items.Count);

            if (sample.Occurrence.Application is { } app)
            {
                _context.Set<Counter>().Add(new Counter { Key = ErrorGroupKeys.HourlyAppKey(effective.Fingerprint, app, hour), Value = items.Count });
            }
        }
    }

    // When the source is already at the distinct-group cap, a genuinely-new fingerprint collapses into the
    // per-source {other} bucket so a hostile/buggy client can't explode the row count (§8.19/§8.27).
    private ResolvedOccurrence ResolveCardinality(ResolvedOccurrence item, Dictionary<string, ErrorGroup> groups, Dictionary<ErrorSource, int> sourceCounts)
    {
        if (groups.ContainsKey(item.Fingerprint))
        {
            return item;
        }

        var source = item.Occurrence.Source;
        if (sourceCounts.GetValueOrDefault(source) < _configuration.MaxDistinctErrorGroups)
        {
            return item;
        }

        // Past the cap, a genuinely-new fingerprint collapses into {other} and stops surfacing as its own issue.
        // Warn once per source per tick so this isn't silent (mirrors the adapter/endpoint CardinalityGuard).
        if (_cardinalityCappedWarned.Add(source))
        {
            _logger.LogWarning(
                "Error grouping hit the {Cap}-issue cap for source {Source}; new fingerprints are now folding into the {{other}} bucket and won't appear as distinct issues. Raise MaxDistinctErrorGroups or investigate the error diversity.",
                _configuration.MaxDistinctErrorGroups,
                source);
        }

        return item with
        {
            Fingerprint = OtherFingerprint(source),
            ExceptionType = OtherToken,
            Title = OtherToken,
            Culprit = OtherToken,
        };
    }

    private void UpsertGroup(ResolvedOccurrence effective, List<ResolvedOccurrence> items, Dictionary<string, ErrorGroup> groups, Dictionary<ErrorSource, int> sourceCounts, DateTime now)
    {
        var occurrences = items.ConvertAll(x => x.Occurrence);
        var count = occurrences.Count;
        var lastSeen = occurrences.Max(x => x.Timestamp);
        var latest = occurrences.MaxBy(x => x.Timestamp)!;

        if (groups.TryGetValue(effective.Fingerprint, out var group))
        {
            group.Count += count;

            if (latest.Version is not null)
            {
                group.LastSeenVersion = latest.Version;
            }

            if (lastSeen > group.LastSeenAt)
            {
                group.LastSeenAt = lastSeen;
                group.SampleTraceId = latest.TraceId;
                if (_configuration.CaptureErrorSamples)
                {
                    group.LastSample = BuildSample(latest);
                }
            }

            // Rolling recent-occurrences window: prepend this batch (newest first), re-cap to 10. Parse the
            // existing JSON defensively — a bad/foreign payload is treated as empty, never throws (§8.29).
            if (_configuration.CaptureErrorSamples)
            {
                var merged = BuildSampleEntries(occurrences);
                merged.AddRange(ParseSamples(group.RecentSamples));
                group.RecentSamples = SerializeSamples(merged);
            }

            group.ExpireAt = now + _configuration.ErrorGroupRetention;

            // Regression: a Resolved group re-opens only on an occurrence AFTER it was resolved (§8.29).
            if (group.Status == ErrorGroupStatus.Resolved && lastSeen > (group.StatusChangedAt ?? DateTime.MinValue))
            {
                group.Status = ErrorGroupStatus.Unresolved;
                group.StatusChangedAt = now;
                BufferRegression(group);
            }

            return;
        }

        var created = new ErrorGroup
        {
            Fingerprint = effective.Fingerprint,
            Source = effective.Occurrence.Source,
            Kind = effective.Occurrence.Kind,
            ExceptionType = Trim(effective.ExceptionType, 512),
            Title = Trim(effective.Title, 512),
            Culprit = Trim(effective.Culprit, 512),
            StatusCode = effective.Occurrence.StatusCode,
            Application = effective.Occurrence.Application,
            FirstSeenAt = occurrences.Min(x => x.Timestamp),
            LastSeenAt = lastSeen,
            Count = count,
            LastSample = _configuration.CaptureErrorSamples ? BuildSample(latest) : null,
            SampleTraceId = latest.TraceId,
            FirstSeenVersion = latest.Version,
            LastSeenVersion = latest.Version,
            Environment = latest.Environment,
            RecentSamples = _configuration.CaptureErrorSamples ? SerializeSamples(BuildSampleEntries(occurrences)) : null,
            Status = ErrorGroupStatus.Unresolved,
            ExpireAt = now + _configuration.ErrorGroupRetention,
        };

        _context.Set<ErrorGroup>().Add(created);
        groups[effective.Fingerprint] = created;

        // The {other} bucket itself never counts against the cap; real groups advance it so later new
        // fingerprints in the same tick still respect the limit.
        if (!string.Equals(effective.ExceptionType, OtherToken, StringComparison.Ordinal))
        {
            sourceCounts[created.Source] = sourceCounts.GetValueOrDefault(created.Source) + 1;
        }
    }

    private ResolvedOccurrence Resolve(ErrorOccurrence occurrence)
    {
        if (occurrence.Kind == ErrorKind.StatusCode)
        {
            var type = $"HTTP {occurrence.StatusCode}";

            return new ResolvedOccurrence(occurrence, ErrorFingerprint.ComputeForStatusCode(occurrence.StatusCode ?? 0, occurrence.Culprit), type, occurrence.Culprit, occurrence.Culprit);
        }

        var locus = ErrorFingerprint.ExtractTopFrame(occurrence.Stack, [.. _configuration.InAppNamespaceDenylist]) ?? occurrence.Culprit;
        var fingerprint = ErrorFingerprint.Compute(occurrence.Source, occurrence.ExceptionType, locus);
        var title = ErrorFingerprint.NormalizeMessage(occurrence.Message);

        return new ResolvedOccurrence(occurrence, fingerprint, occurrence.ExceptionType, title, occurrence.Culprit);
    }

    private void BufferRegression(ErrorGroup group)
    {
        _pendingRegressions.Add(new IssueRegressedEvent
        {
            Type = WarpEventType.IssueRegressed,
            Severity = WarpEventSeverity.Warning,
            TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
            MachineName = Environment.MachineName,
            Application = group.Application,
            Message = $"Issue regressed: {group.ExceptionType} in {group.Culprit}",
            Fingerprint = group.Fingerprint,
            Source = group.Source,
            ExceptionType = group.ExceptionType,
            Culprit = group.Culprit,
        });
    }

    private static string OtherFingerprint(ErrorSource source)
        => ErrorFingerprint.Compute(source, OtherToken, OtherToken);

    private static string? BuildSample(ErrorOccurrence occurrence)
    {
        var sample = occurrence.Message ?? string.Empty;
        if (occurrence.Stack is { Length: > 0 } stack)
        {
            sample = sample.Length > 0 ? $"{sample}\n{stack}" : stack;
        }

        return sample.Length == 0 ? null : Trim(sample, 4096);
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static List<ErrorSampleEntry> BuildSampleEntries(List<ErrorOccurrence> occurrences)
        => [.. occurrences
            .OrderByDescending(x => x.Timestamp)
            .Take(MaxSamples)
            .Select(x =>
                new ErrorSampleEntry(x.TraceId, x.Timestamp, TrimMessage(x.Message), x.Version)),];

    private static List<ErrorSampleEntry> ParseSamples(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ErrorSampleEntry>>(json, AggregatorSampleJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? SerializeSamples(List<ErrorSampleEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var capped = entries.Count > MaxSamples ? entries.GetRange(0, MaxSamples) : entries;
        var json = JsonSerializer.Serialize(capped, AggregatorSampleJson.Options);

        // Drop the oldest entries until the payload fits the soft length budget (~4000 chars).
        while (json.Length > MaxSamplesJsonLength && capped.Count > 1)
        {
            capped = capped.GetRange(0, capped.Count - 1);
            json = JsonSerializer.Serialize(capped, AggregatorSampleJson.Options);
        }

        return json;
    }

    private static string? TrimMessage(string? message)
        => message is null ? null : Trim(message, SampleMessageMax);

    private sealed record ResolvedOccurrence(ErrorOccurrence Occurrence, string Fingerprint, string ExceptionType, string Title, string Culprit);

    private sealed record ErrorSampleEntry(Guid? TraceId, DateTime Timestamp, string? Message, string? Version);
}

/// <summary>Shared camelCase options for the <c>RecentSamples</c> JSON (§8.29) — non-generic to avoid a static field in a generic type (S2743).</summary>
internal static class AggregatorSampleJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
