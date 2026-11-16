using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>The status rule of a seeded health-board tile (#326): none, a threshold, or a deviation from a baseline.</summary>
public sealed class MetricTileCheck
{
    private MetricTileCheck(MetricTileStatusRule rule, SloComparison? comparison = null, double? threshold = null, int? baselineDays = null, double? maxDeviation = null, MetricDeviationDirection? direction = null)
    {
        Rule = rule;
        Comparison = comparison;
        Threshold = threshold;
        BaselineDays = baselineDays;
        MaxDeviation = maxDeviation;
        Direction = direction;
    }

    /// <summary>No status colour: the tile shows its numbers only.</summary>
    public static MetricTileCheck None { get; } = new(MetricTileStatusRule.None);

    public MetricTileStatusRule Rule { get; }

    public SloComparison? Comparison { get; }

    public double? Threshold { get; }

    public int? BaselineDays { get; }

    public double? MaxDeviation { get; }

    public MetricDeviationDirection? Direction { get; }

    /// <summary>The value must stay at most <paramref name="threshold"/> (a 0–1 fraction on a ratio tile).</summary>
    public static MetricTileCheck AtMost(double threshold) => new(MetricTileStatusRule.Threshold, SloComparison.AtMost, threshold);

    /// <summary>The value must stay at least <paramref name="threshold"/> (a 0–1 fraction on a ratio tile).</summary>
    public static MetricTileCheck AtLeast(double threshold) => new(MetricTileStatusRule.Threshold, SloComparison.AtLeast, threshold);

    /// <summary>
    /// The value must not move more than <paramref name="maxDeviation"/> from the same window <paramref name="baselineDays"/>
    /// (1 or 7) earlier, in <paramref name="direction"/>: a fraction of the baseline on a value tile (0.3 = 30 %), ratio
    /// points on a ratio tile (0.02 = 2 points).
    /// </summary>
    public static MetricTileCheck Deviation(double maxDeviation, int baselineDays = 7, MetricDeviationDirection direction = MetricDeviationDirection.Either)
        => new(MetricTileStatusRule.Deviation, baselineDays: baselineDays, maxDeviation: maxDeviation, direction: direction);

    internal void ApplyTo(MetricBoardTile tile)
    {
        tile.StatusRule = Rule;
        tile.Comparison = Comparison;
        tile.Threshold = Threshold;
        tile.BaselineDays = BaselineDays;
        tile.MaxDeviation = MaxDeviation;
        tile.DeviationDirection = Direction;
    }
}

/// <summary>
/// Tiles seeded from config via <c>opt.AddMetricBoard(b =&gt; b.Value(...).Ratio(...))</c> (#326), in board order. Seeding
/// is insert-if-absent (matched on name, or on instrument + kind + application + filter + ratio tag + aggregate), so a
/// later dashboard edit is never clobbered on restart — the DB row wins. A tile deleted in the dashboard is seeded again
/// on the next start while it stays in code.
/// </summary>
public sealed class MetricBoardOptions
{
    internal List<MetricBoardTile> Seeded { get; } = [];

    /// <summary>
    /// A value tile: the matching events of <paramref name="instrument"/> over the window (sum for a counter or up-down
    /// counter, measurement count for a histogram), or a gauge's <paramref name="aggregate"/> (average by default).
    /// </summary>
    public MetricBoardOptions Value(
        string name,
        string instrument,
        string? tagFilter = null,
        string? application = null,
        SloGaugeAggregate? aggregate = null,
        MetricTileCheck? check = null)
        => Add(
            new MetricBoardTile
            {
                Name = name,
                Instrument = instrument,
                TagFilter = tagFilter,
                Application = application,
                Kind = MetricTileKind.Value,
                GaugeAggregate = aggregate,
            },
            check);

    /// <summary>
    /// A ratio tile: events of <paramref name="instrument"/> whose <paramref name="ratioTag"/> matches
    /// <paramref name="goodValues"/> over good + <paramref name="badValues"/> (CSV, trailing <c>*</c> allowed, no overlap).
    /// Counters and histograms only.
    /// </summary>
    public MetricBoardOptions Ratio(
        string name,
        string instrument,
        string ratioTag,
        string goodValues,
        string badValues,
        string? tagFilter = null,
        string? application = null,
        MetricTileCheck? check = null)
        => Add(
            new MetricBoardTile
            {
                Name = name,
                Instrument = instrument,
                TagFilter = tagFilter,
                Application = application,
                Kind = MetricTileKind.Ratio,
                RatioTag = ratioTag,
                GoodValues = goodValues,
                BadValues = badValues,
            },
            check);

    // The seeder's field match: a tile whose name was edited in the dashboard is still found by what it reads.
    internal static bool SameFields(MetricBoardTile a, MetricBoardTile b)
        => string.Equals(a.Instrument, b.Instrument, StringComparison.Ordinal)
            && a.Kind == b.Kind
            && string.Equals(a.Application, b.Application, StringComparison.Ordinal)
            && string.Equals(a.TagFilter, b.TagFilter, StringComparison.Ordinal)
            && string.Equals(a.RatioTag, b.RatioTag, StringComparison.Ordinal)
            && a.GaugeAggregate == b.GaugeAggregate;

    private MetricBoardOptions Add(MetricBoardTile tile, MetricTileCheck? check)
    {
        (check ?? MetricTileCheck.None).ApplyTo(tile);

        // The registry kind is unknown here; the seeder re-validates against it.
        if (!MetricBoardValidation.TryValidate(tile, null, out var error))
        {
            throw new ArgumentException($"Invalid metric board tile '{tile.Name}': {error}", nameof(tile));
        }

        // The seeder matches by name or by fields, so a repeat of either would silently seed only the first.
        if (Seeded.Exists(x => string.Equals(x.Name, tile.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Duplicate metric board tile name '{tile.Name}': tile names must be unique.", nameof(tile));
        }

        if (Seeded.Exists(x => SameFields(x, tile)))
        {
            throw new ArgumentException($"Metric board tile '{tile.Name}' reads the same instrument, kind, application, filter, ratio tag and aggregate as another tile.", nameof(tile));
        }

        if (Seeded.Count >= MetricBoardValidation.MaxTiles)
        {
            throw new ArgumentException($"A metric board holds at most {MetricBoardValidation.MaxTiles} tiles.", nameof(tile));
        }

        Seeded.Add(tile);

        return this;
    }
}

/// <summary>
/// Seeds the metrics health board (#326) from code, inside the <c>AddWarp</c> / <c>AddWarpServer</c> lambda:
/// <code>
/// opt.AddMetricBoard(b =&gt; b
///     .Ratio("Payment success", "payment.results", "result", "ok", "declined,error", check: MetricTileCheck.Deviation(0.02, direction: MetricDeviationDirection.Drop))
///     .Value("5xx", "http.server.request.duration", tagFilter: "http.response.status_code=5*", check: MetricTileCheck.AtMost(0)));
/// </code>
/// Separate from <c>AddMetrics</c> on purpose: that starts the collector and flusher, which a dashboard-only host has no
/// use for. The board's read/command services and the <c>metric_board_tile</c> table are always present (§2.11) — this
/// only registers the insert-if-absent seeder. Calling it more than once appends to one board.
/// </summary>
public static class MetricBoardServiceConfiguration
{
    public static IWarpBuilder AddMetricBoard(this IWarpBuilder builder, Action<MetricBoardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var existing = builder.Services
            .Where(x => x.ServiceType == typeof(MetricBoardOptions))
            .Select(x => x.ImplementationInstance)
            .OfType<MetricBoardOptions>()
            .FirstOrDefault();
        var options = existing ?? new MetricBoardOptions();
        configure(options);

        if (options.Seeded.Count == 0)
        {
            return builder;
        }

        if (existing is null)
        {
            builder.Services.AddSingleton(options);
        }

        var seederType = typeof(MetricBoardSeeder<>).MakeGenericType(ResolveContextType(builder));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), seederType));

        return builder;
    }

    private static Type ResolveContextType(IWarpBuilder builder)
        => builder.GetType()
            .GetInterfaces()
            .Where(x => x.IsGenericType)
            .Where(x => x.GetGenericTypeDefinition() == typeof(IWarpBuilder<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "AddMetricBoard() could not determine the DbContext type from the Warp builder. Call it inside the "
                + "AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda.");
}

/// <summary>
/// Startup seeder for config-defined health-board tiles (#326), the <c>SloSeeder</c> shape: insert-if-absent by name or
/// by fields (<see cref="MetricBoardOptions.SameFields"/>), so a dashboard edit of either survives a restart. Each seed
/// is validated against the registry kind of its instrument (a ratio on a gauge is skipped, logged at Error). The schema
/// is migrator-gated present, so a failure is a real error, logged at Error and non-fatal (tiles can still be authored
/// in the dashboard).
/// </summary>
internal sealed class MetricBoardSeeder<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricBoardOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricBoardSeeder<TContext>> _logger;

    public MetricBoardSeeder(IServiceScopeFactory scopeFactory, MetricBoardOptions options, TimeProvider timeProvider, ILogger<MetricBoardSeeder<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricQueryService>();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var stored = await context.Set<MetricBoardTile>()
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var position = stored.Count == 0 ? 0 : stored.Max(x => x.Position);
            var count = stored.Count;
            var added = 0;

            foreach (var seed in _options.Seeded)
            {
                // Validated first, so the match sees the normalized fields the stored row was written with (a gauge's
                // default aggregate). Matched in memory, ordinally: the board is a handful of rows, and a
                // case-insensitive collation must not make two tiles that differ only by case read as one.
                var tile = Copy(seed);
                var (ok, error) = await MetricBoardTileValidator.ValidateAsync(tile, metrics, cancellationToken);
                if (stored.Exists(x => string.Equals(x.Name, tile.Name, StringComparison.Ordinal) || MetricBoardOptions.SameFields(x, tile)))
                {
                    continue;
                }

                if (!ok)
                {
                    _logger.LogError("Metric board tile '{Name}' was NOT seeded: {Error}", seed.Name, error);

                    continue;
                }

                if (count >= MetricBoardValidation.MaxTiles)
                {
                    _logger.LogError("Metric board tile '{Name}' was NOT seeded: the board already holds {Max} tiles.", seed.Name, MetricBoardValidation.MaxTiles);

                    continue;
                }

                tile.Position = ++position;
                tile.CreatedAt = now;
                tile.UpdatedAt = now;
                context.Set<MetricBoardTile>().Add(tile);
                stored.Add(tile);
                count++;
                added++;
            }

            if (added > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Seeded {Count} metric board tile(s) from config", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metric board seeding failed — {Count} config-declared tile(s) were NOT seeded and will be missing until created in the dashboard. The schema is expected present (migrator gating), so this is a real error, not a startup race.", _options.Seeded.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The options keep their own instances: a seed is copied so the tracked entity never aliases the singleton.
    private static MetricBoardTile Copy(MetricBoardTile x) => new()
    {
        Name = x.Name,
        Instrument = x.Instrument,
        Application = x.Application,
        TagFilter = x.TagFilter,
        Kind = x.Kind,
        RatioTag = x.RatioTag,
        GoodValues = x.GoodValues,
        BadValues = x.BadValues,
        GaugeAggregate = x.GaugeAggregate,
        StatusRule = x.StatusRule,
        Comparison = x.Comparison,
        Threshold = x.Threshold,
        BaselineDays = x.BaselineDays,
        MaxDeviation = x.MaxDeviation,
        DeviationDirection = x.DeviationDirection,
    };
}
