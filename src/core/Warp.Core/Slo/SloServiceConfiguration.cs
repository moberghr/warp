using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Slo;

/// <summary>Presence marker for the dashboard <c>slo</c> addon flag — registered by <see cref="SloServiceConfiguration.AddSlo"/>.</summary>
public interface ISloMarker;

/// <inheritdoc cref="ISloMarker"/>
public sealed class SloMarker : ISloMarker;

/// <summary>
/// Objectives seeded from config via <c>opt.AddSlo(o =&gt; o.AddObjective(...))</c> (§8.31). Seeding is
/// insert-if-absent (matched on kind + dimension + application + percentile), so a later dashboard edit to the
/// same objective is never clobbered on restart — the DB row wins. Repeated <c>AddSlo</c> calls add to one instance
/// (one seeder), so the duplicate-name check spans all of them.
/// </summary>
public sealed class SloOptions
{
    internal List<SloDefinition> Seeded { get; } = [];

    public SloOptions AddObjective(
        SloKind kind,
        string dimension,
        double target,
        int windowSeconds = 3600,
        int? percentile = null,
        string? application = null,
        string? name = null,
        bool enabled = true)
    {
        var definition = new SloDefinition
        {
            Name = name ?? $"{kind} {dimension}",
            Kind = kind,
            Dimension = dimension,
            TargetValue = target,
            WindowSeconds = windowSeconds,
            Percentile = percentile,
            Application = application,
            Enabled = enabled,
        };

        if (!SloValidation.TryValidate(definition, out var error))
        {
            throw new ArgumentException($"Invalid SLO objective ({kind} {dimension}): {error}", nameof(target));
        }

        Seeded.Add(definition);

        return this;
    }

    /// <summary>
    /// Good-event ratio over a custom metric (<paramref name="instrument"/>): events whose <paramref name="ratioTag"/>
    /// matches <paramref name="goodValues"/> over good + <paramref name="badValues"/> (CSV, trailing <c>*</c> allowed)
    /// must stay at or above <paramref name="target"/>. With <paramref name="baselineDays"/> (1 or 7) the target is
    /// the maximum drop in ratio points against the same window that many days earlier.
    /// </summary>
    public SloOptions AddMetricRatio(
        string name,
        string instrument,
        string ratioTag,
        string goodValues,
        string badValues,
        double target,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        int? baselineDays = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricRatio,
            Dimension = instrument,
            TargetValue = target,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            RatioTag = ratioTag,
            GoodValues = goodValues,
            BadValues = badValues,
            BaselineDays = baselineDays,
            Enabled = enabled,
        });

    /// <summary>Matching events of <paramref name="instrument"/> in the window must not exceed <paramref name="maxEvents"/> (0 allowed).</summary>
    public SloOptions AddMetricThreshold(
        string name,
        string instrument,
        double maxEvents,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricThreshold,
            Dimension = instrument,
            TargetValue = maxEvents,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            Enabled = enabled,
        });

    /// <summary>Matching events must stay at or above <paramref name="minRatio"/> times the same window <paramref name="baselineDays"/> (1 or 7) earlier.</summary>
    public SloOptions AddMetricVolumeFloor(
        string name,
        string instrument,
        double minRatio,
        int baselineDays,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricVolumeFloor,
            Dimension = instrument,
            TargetValue = minRatio,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            BaselineDays = baselineDays,
            Enabled = enabled,
        });

    /// <summary>Events of <paramref name="instrument"/> minus events of <paramref name="secondaryMetric"/> must not exceed <paramref name="maxGap"/> (0 allowed).</summary>
    public SloOptions AddMetricGap(
        string name,
        string instrument,
        string secondaryMetric,
        double maxGap,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        string? secondaryTagFilter = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricGap,
            Dimension = instrument,
            TargetValue = maxGap,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            SecondaryMetric = secondaryMetric,
            SecondaryTagFilter = secondaryTagFilter,
            Enabled = enabled,
        });

    /// <summary>
    /// Latency over a histogram <paramref name="instrument"/>: at least <paramref name="percentile"/>% (1–99) of matching
    /// events must fall in buckets whose upper bound is at most <paramref name="threshold"/>, in the instrument's own
    /// unit (seconds for <c>http.server.request.duration</c>). The threshold is compared with bucket upper bounds, so
    /// pick a value on the instrument's bucket ladder: a threshold between two bounds behaves like the lower one.
    /// </summary>
    public SloOptions AddMetricLatency(
        string name,
        string instrument,
        double threshold,
        int percentile = SloValidation.DefaultLatencyPercentile,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricLatency,
            Dimension = instrument,
            TargetValue = threshold,
            Percentile = percentile,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            Enabled = enabled,
        });

    /// <summary>
    /// A gauge <paramref name="instrument"/>'s per-bucket <paramref name="aggregate"/> (average by default: samples from
    /// every process and matching series are averaged, never summed) must stay at most — or, with
    /// <see cref="SloComparison.AtLeast"/>, at least — <paramref name="threshold"/> in at least
    /// <paramref name="percentile"/>% (1–99) of the window's observed time.
    /// </summary>
    public SloOptions AddMetricGaugeThreshold(
        string name,
        string instrument,
        double threshold,
        SloComparison comparison = SloComparison.AtMost,
        SloGaugeAggregate aggregate = SloGaugeAggregate.Average,
        int percentile = SloValidation.DefaultLatencyPercentile,
        int windowSeconds = 3600,
        string? application = null,
        string? tagFilter = null,
        bool enabled = true)
        => AddMetric(new SloDefinition
        {
            Name = name,
            Kind = SloKind.MetricGaugeThreshold,
            Dimension = instrument,
            TargetValue = threshold,
            Comparison = comparison,
            GaugeAggregate = aggregate,
            Percentile = percentile,
            WindowSeconds = windowSeconds,
            Application = application,
            TagFilter = tagFilter,
            Enabled = enabled,
        });

    // The retention check (R29) needs WarpConfiguration, which the options do not have: the seeder re-validates with it.
    private SloOptions AddMetric(SloDefinition definition)
    {
        if (!SloValidation.TryValidate(definition, out var error))
        {
            throw new ArgumentException($"Invalid SLO objective ({definition.Kind} {definition.Dimension}): {error}", nameof(definition));
        }

        // The seeder matches a metric objective by name, so a repeated name would silently seed only the first.
        // Case-insensitive, the comparer the seeder matches names with (in memory, so the database collation never
        // decides): "Payments" and "payments" are one objective. Checked across every AddSlo call, which merge here.
        if (Seeded.Exists(x => x.Kind == definition.Kind
            && string.Equals(x.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Application, definition.Application, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Duplicate SLO objective name '{definition.Name}' for {definition.Kind}: metric objective names must be unique per kind and application, ignoring case.", nameof(definition));
        }

        Seeded.Add(definition);

        return this;
    }
}

/// <summary>
/// Opt-in for SLO evaluation (§8.31), called inside the <c>AddWarp</c> / <c>AddWarpServer</c> lambda:
/// <c>opt.AddSlo(o =&gt; o.AddObjective(SloKind.SuccessRate, "MyJob", 0.995))</c>. Registers the presence marker
/// (which lights the dashboard <c>slo</c> flag) and, when objectives are config-seeded, a startup seeder. The
/// <c>SloEvaluator</c> server task, the read/command services, and the <c>SloDefinition</c>/<c>SloEvaluation</c>
/// tables are always present (§2.11) — this method only turns evaluation/authoring on.
/// </summary>
public static class SloServiceConfiguration
{
    public static IWarpBuilder AddSlo(this IWarpBuilder builder, Action<SloOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ISloMarker, SloMarker>();

        // Repeated calls merge into the one registered SloOptions, as AddMetricBoard does: one seeder, and a duplicate
        // metric objective name across two calls throws like one within a call.
        var existing = builder.Services
            .Where(x => x.ServiceType == typeof(SloOptions))
            .Select(x => x.ImplementationInstance)
            .OfType<SloOptions>()
            .FirstOrDefault();
        var options = existing ?? new SloOptions();
        configure?.Invoke(options);

        if (options.Seeded.Count == 0)
        {
            return builder;
        }

        if (existing is null)
        {
            builder.Services.AddSingleton(options);
        }

        var seederType = typeof(SloSeeder<>).MakeGenericType(ResolveContextType(builder));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), seederType));

        return builder;
    }

    private static Type ResolveContextType(IWarpBuilder builder)
        => builder.GetType()
            .GetInterfaces()
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IWarpBuilder<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "AddSlo() could not determine the DbContext type from the Warp builder. Call it inside the "
                + "AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda.");
}

/// <summary>
/// Startup seeder for config-defined SLO objectives (§8.31). Insert-if-absent by (kind, dimension, application,
/// percentile; metric kinds also by name or filter fields, #332; latency and gauge objectives' fields include the threshold) so it never clobbers a dashboard edit. Runs once at boot; the schema is expected present because
/// web/worker are gated on a dedicated migrator running to completion first (§ migrator gating), so a failure here
/// is not a transient not-yet-migrated race but a real error — logged at <c>Error</c> (alertable) rather than
/// swallowed to a Warning nobody watches. It stays non-fatal (objectives can still be authored in the dashboard),
/// but the loud log means "objectives were NOT seeded" is visible instead of a silent half-configured feature.
/// </summary>
internal sealed class SloSeeder<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SloOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;
    private readonly ILogger<SloSeeder<TContext>> _logger;

    public SloSeeder(IServiceScopeFactory scopeFactory, SloOptions options, TimeProvider timeProvider, IOptions<WarpConfiguration> configuration, ILogger<SloSeeder<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _logger = logger;
    }

    // Test seam: runs inside the lock after the existence checks and before the inserts are saved, so a race test can pin
    // one seeder there (N=2 BarrierSignal) and prove the other cannot check until it commits.
    internal Func<CancellationToken, Task>? BeforeSave { get; init; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricQueryService>();
            var queries = scope.ServiceProvider.GetService<IWarpSqlQueries<TContext>>();
            var valid = new List<SloDefinition>();

            foreach (var seed in _options.Seeded)
            {
                // The same validator as the dashboard upsert: a metric objective is checked against the registry kind
                // of its instruments (a gauge, primary or a gap's secondary, has no event count) and the retention.
                if (SloValidation.IsMetricKind(seed.Kind))
                {
                    var (ok, metricError) = await SloDefinitionValidator.ValidateAsync(seed, metrics, _configuration.HourlyStatisticsRetention, cancellationToken);
                    if (!ok)
                    {
                        _logger.LogError("SLO objective '{Name}' was NOT seeded: {Error}", seed.Name, metricError);

                        continue;
                    }
                }

                valid.Add(seed);
            }

            // Under the transaction-scoped warp:slo-seed lock (§2.16), so N replicas starting at once cannot each find an objective missing and each insert it.
            var added = await TransactionLock.RunAsync(queries, context, TransactionLock.SloSeedKey, TransactionLock.SeedTimeout, (x, ct) => SeedAsync(x, valid, ct), cancellationToken);
            if (added > 0)
            {
                _logger.LogInformation("Seeded {Count} SLO objective(s) from config", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SLO objective seeding failed — {Count} config-declared objective(s) were NOT seeded and will be missing until created in the dashboard. The schema is expected present (migrator gating), so this is a real error, not a startup race.", _options.Seeded.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The names of the matched fields a stored objective and a seed disagree on, for the skip log: field names only,
    // never a value (§1.2: a filter holds tag values).
    internal static IEnumerable<string> DifferingFields(SloDefinition stored, SloDefinition seed)
    {
        var fields = new (string Name, bool Same)[]
        {
            (nameof(SloDefinition.Dimension), string.Equals(stored.Dimension, seed.Dimension, StringComparison.Ordinal)),
            (nameof(SloDefinition.Percentile), stored.Percentile == seed.Percentile),
            (nameof(SloDefinition.TagFilter), string.Equals(stored.TagFilter, seed.TagFilter, StringComparison.Ordinal)),
            (nameof(SloDefinition.SecondaryMetric), string.Equals(stored.SecondaryMetric, seed.SecondaryMetric, StringComparison.Ordinal)),
            (nameof(SloDefinition.RatioTag), string.Equals(stored.RatioTag, seed.RatioTag, StringComparison.Ordinal)),
            (nameof(SloDefinition.Comparison), stored.Comparison == seed.Comparison),
            (nameof(SloDefinition.GaugeAggregate), stored.GaugeAggregate == seed.GaugeAggregate),
            (nameof(SloDefinition.TargetValue), Math.Abs(stored.TargetValue - seed.TargetValue) <= Math.Abs(seed.TargetValue) * 1e-9),
        };

        return fields
            .Where(x => !x.Same)
            .Select(x => x.Name);
    }

    private async Task<int> SeedAsync(DbContext context, List<SloDefinition> seeds, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var added = 0;

        // One read of the candidates, matched in memory with one comparer: in SQL the database collation would decide
        // (SQL Server's default is case-insensitive, PostgreSQL's is not), so the same seeds could match on one provider
        // and insert on the other. The objectives table is a handful of rows per kind.
        var kinds = seeds
            .Select(x => x.Kind)
            .Distinct()
            .ToList();
        var stored = await context.Set<SloDefinition>()
            .AsNoTracking()
            .Where(x => kinds.Contains(x.Kind))
            .ToListAsync(ct);

        foreach (var seed in seeds)
        {
            // Metric kinds put the instrument in Dimension, so two objectives on one instrument are told apart by their
            // filter, secondary instrument and ratio tag. Those are editable in the dashboard, so the name matches too
            // (#332): an edit of either the name or the fields still finds the row; only an edit of both reseeds. Names
            // are unique only among the config seeds (SloOptions rejects a repeat per kind and application,
            // case-insensitively); the table has no unique index, so a dashboard-created objective may share one. A
            // latency or gauge objective also matches on its threshold (and a gauge on its comparison and aggregate): a
            // warning tier (p95 <= 0.5) and a paging tier (p95 <= 1) on one instrument differ only there, and without it
            // a tier added in a later deploy would match the existing one and never be seeded. Both fields are null on
            // every other kind, so they match there as before.
            if (!SloValidation.IsMetricKind(seed.Kind))
            {
                if (stored.Exists(x => x.Kind == seed.Kind
                    && string.Equals(x.Dimension, seed.Dimension, StringComparison.Ordinal)
                    && string.Equals(x.Application, seed.Application, StringComparison.Ordinal)
                    && x.Percentile == seed.Percentile))
                {
                    continue;
                }
            }
            else if (MetricSeedPresent(stored, seed))
            {
                continue;
            }

            // Not added to `stored`: a seed matches only rows that were in the database when the pass began (other
            // replicas' commits, earlier boots, dashboard edits), never a seed inserted earlier in this pass. Two config
            // objectives sharing a match key on purpose (QueueWaitLatency tiers on one queue that differ by target, a
            // 1 h and a 24 h SuccessRate on one job type) are both inserted on a fresh database, as before the match moved
            // into memory. The target and window stay out of the non-metric key: those kinds have no name fallback, so
            // a dashboard edit of either would make every restart insert a duplicate.
            seed.CreatedAt = now;
            seed.UpdatedAt = now;
            context.Set<SloDefinition>().Add(seed);
            added++;
        }

        if (BeforeSave is not null)
        {
            await BeforeSave(ct);
        }

        if (added > 0)
        {
            await context.SaveChangesAsync(ct);
        }

        return added;
    }

    // Fields match ordinally (an instrument, filter or tag is data, case and all); the name case-insensitively, the
    // comparer SloOptions rejects a repeated name with.
    private bool MetricSeedPresent(List<SloDefinition> stored, SloDefinition seed)
    {
        var matchesTarget = seed.Kind is SloKind.MetricLatency or SloKind.MetricGaugeThreshold;
        var targetTolerance = Math.Abs(seed.TargetValue) * 1e-9;
        var candidates = stored
            .Where(x => x.Kind == seed.Kind)
            .Where(x => string.Equals(x.Application, seed.Application, StringComparison.Ordinal))
            .ToList();
        var sameFields = candidates.Exists(x => string.Equals(x.Dimension, seed.Dimension, StringComparison.Ordinal)
            && x.Percentile == seed.Percentile
            && string.Equals(x.TagFilter, seed.TagFilter, StringComparison.Ordinal)
            && string.Equals(x.SecondaryMetric, seed.SecondaryMetric, StringComparison.Ordinal)
            && string.Equals(x.RatioTag, seed.RatioTag, StringComparison.Ordinal)
            && x.Comparison == seed.Comparison
            && x.GaugeAggregate == seed.GaugeAggregate
            && (!matchesTarget || Math.Abs(x.TargetValue - seed.TargetValue) <= targetTolerance));
        if (sameFields)
        {
            return true;
        }

        var named = candidates.Find(x => string.Equals(x.Name, seed.Name, StringComparison.OrdinalIgnoreCase));
        if (named is null)
        {
            return false;
        }

        // The stored objective wins (a dashboard edit survives a restart), but say so: a changed seed that never takes
        // effect is otherwise silent. Information, not Warning: an intended dashboard edit logs this every boot.
        var differing = DifferingFields(named, seed)
            .Where(x => matchesTarget || !string.Equals(x, nameof(SloDefinition.TargetValue), StringComparison.Ordinal));
        _logger.LogInformation(
            "SLO objective '{Name}' ({Kind}) was not seeded: a stored objective already has that name and differs in {Fields}. The stored objective is kept; rename the seed or delete the stored objective to apply the config.",
            seed.Name,
            seed.Kind,
            string.Join(", ", differing));

        return true;
    }
}
