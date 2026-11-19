using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
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
/// Tiles seeded from config via <c>opt.AddMetricBoard(b =&gt; b.Value(...).Ratio(...).Chart(...))</c> (#326), in board
/// order. Seeding is insert-if-absent (matched on name, or on instrument + kind + application + filter + ratio tag +
/// aggregate + chart query), so a
/// later dashboard edit is never clobbered on restart — the DB row wins. A tile deleted in the dashboard is seeded again
/// on the next start while it stays in code.
/// </summary>
public sealed class MetricBoardOptions
{
    internal List<MetricBoardTile> Seeded { get; } = [];

    // The objective each seeded SLO tile names, keyed by the seed instance: the seeder resolves it to an id at seed time.
    internal Dictionary<MetricBoardTile, SloTileSeed> SloSeeds { get; } = new(ReferenceEqualityComparer.Instance);

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

    /// <summary>
    /// A chart tile: the metric page's chart of <paramref name="instrument"/> with these controls, over the board's range.
    /// The parameters are the page's own (<paramref name="filter"/> in the <c>MetricFilter</c> grammar <c>k=v[,k=v]</c>,
    /// trailing <c>*</c>; <paramref name="compare"/> <c>1d</c>, <c>7d</c> or <c>1d,7d</c>; <paramref name="percentile"/>
    /// 50, 90, 95 or 99 on a histogram (the percentiles the page draws); a <paramref name="numerator"/> pattern turns on the ratio view of the breakdown;
    /// <paramref name="minMax"/> draws a gauge's min / max lines; <paramref name="rate"/> <c>minute</c> or <c>second</c>),
    /// stored as the page's URL query. A chart has no status rule. At most
    /// <see cref="MetricBoardValidation.MaxChartTiles"/> per board.
    /// </summary>
    public MetricBoardOptions Chart(
        string name,
        string instrument,
        string? application = null,
        string? breakdown = null,
        string? filter = null,
        string? compare = null,
        int? percentile = null,
        string? numerator = null,
        bool minMax = false,
        string? rate = null)
        => Add(
            new MetricBoardTile
            {
                Name = name,
                Instrument = instrument,
                Kind = MetricTileKind.Chart,
                ChartQuery = MetricChartQuery.Build(application, breakdown, filter, compare, percentile, numerator, minMax, rate),
            },
            null);

    /// <summary>
    /// An SLO tile showing the objective named <paramref name="name"/> (matched ignoring case) of
    /// <paramref name="application"/> (matched exactly; null matches an objective with no application). The tile is
    /// labelled <paramref name="label"/>, the objective's name by default (tile names are unique per board, so two
    /// applications' objectives of one name need a label each). The objective is resolved when the board is seeded,
    /// after the SLO seeder: no match, or more than one, logs at Error and skips the tile. An SLO tile has no status rule
    /// of its own.
    /// </summary>
    public MetricBoardOptions Slo(string name, string? application = null, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("An SLO tile needs the objective's name, at most 200 characters.", nameof(name));
        }

        var objective = new SloTileSeed(name.Trim(), string.IsNullOrWhiteSpace(application) ? null : application.Trim());
        if (objective.Application?.Length > 200)
        {
            throw new ArgumentException("Application must be at most 200 characters.", nameof(application));
        }

        if (SloSeeds.Values.Any(x => string.Equals(x.Name, objective.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Application, objective.Application, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Metric board SLO tile '{objective.Name}' names the same objective as another tile.", nameof(name));
        }

        var tile = new MetricBoardTile
        {
            Name = string.IsNullOrWhiteSpace(label) ? objective.Name : label.Trim(),
            Kind = MetricTileKind.Slo,
        };
        if (tile.Name.Length > 200)
        {
            throw new ArgumentException("An SLO tile's label must be at most 200 characters.", nameof(label));
        }

        Append(tile);
        SloSeeds[tile] = objective;

        return this;
    }

    // The seeder's field match: a tile whose name was edited in the dashboard is still found by what it reads.
    internal static bool SameFields(MetricBoardTile a, MetricBoardTile b)
        => string.Equals(a.Instrument, b.Instrument, StringComparison.Ordinal)
            && a.Kind == b.Kind
            && string.Equals(a.Application, b.Application, StringComparison.Ordinal)
            && string.Equals(a.TagFilter, b.TagFilter, StringComparison.Ordinal)
            && string.Equals(a.RatioTag, b.RatioTag, StringComparison.Ordinal)
            && a.GaugeAggregate == b.GaugeAggregate
            && string.Equals(a.ChartQuery, b.ChartQuery, StringComparison.Ordinal)
            && a.SloDefinitionId == b.SloDefinitionId;

    private MetricBoardOptions Add(MetricBoardTile tile, MetricTileCheck? check)
    {
        (check ?? MetricTileCheck.None).ApplyTo(tile);

        // The registry kind is unknown here; the seeder re-validates against it.
        if (!MetricBoardValidation.TryValidate(tile, null, out var error))
        {
            throw new ArgumentException($"Invalid metric board tile '{tile.Name}': {error}", nameof(tile));
        }

        Append(tile);

        return this;
    }

    private void Append(MetricBoardTile tile)
    {
        // The seeder matches by name or by fields, so a repeat of either would silently seed only the first.
        if (Seeded.Exists(x => string.Equals(x.Name, tile.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Duplicate metric board tile name '{tile.Name}': tile names must be unique.", nameof(tile));
        }

        // An SLO seed has no objective id yet (it is resolved at seed time): Slo() checks the objective's name instead.
        if (tile.Kind != MetricTileKind.Slo && Seeded.Exists(x => SameFields(x, tile)))
        {
            throw new ArgumentException($"Metric board tile '{tile.Name}' reads the same instrument, kind, application, filter, ratio tag, aggregate and chart query as another tile.", nameof(tile));
        }

        if (tile.Kind == MetricTileKind.Chart && Seeded.Count(x => x.Kind == MetricTileKind.Chart) >= MetricBoardValidation.MaxChartTiles)
        {
            throw new ArgumentException($"A metric board holds at most {MetricBoardValidation.MaxChartTiles} chart tiles.", nameof(tile));
        }

        if (Seeded.Count >= MetricBoardValidation.MaxTiles)
        {
            throw new ArgumentException($"A metric board holds at most {MetricBoardValidation.MaxTiles} tiles.", nameof(tile));
        }

        Seeded.Add(tile);
    }
}

/// <summary>The objective a seeded SLO tile names: its name (matched ignoring case) and application (matched exactly).</summary>
internal sealed record SloTileSeed(string Name, string? Application);

/// <summary>
/// One board to seed from code: the Default board (<see cref="Name"/> null) or a named board, matched by name ignoring case.
/// </summary>
internal sealed class MetricBoardSeed
{
    public string? Name { get; init; }

    public MetricBoardOptions Tiles { get; } = new();
}

/// <summary>Every board seeded from code, in registration order. Repeated <c>AddMetricBoard</c> calls merge into one instance.</summary>
internal sealed class MetricBoardSeeds
{
    /// <summary>
    /// How many background passes retry an SLO tile whose objective did not exist at startup before it is given up
    /// (logged at Error).
    /// </summary>
    public const int MaxRetries = 10;

    /// <summary>How long the background retry of such a tile waits between passes.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    public List<MetricBoardSeed> Boards { get; } = [];

    /// <summary>The seed of board <paramref name="name"/> (null = Default), added when it is the first call for that board.</summary>
    public MetricBoardSeed For(string? name)
    {
        var existing = Boards.Find(x => name is null ? x.Name is null : x.Name is not null && MetricBoardStore.SameName(x.Name, name));
        if (existing is not null)
        {
            return existing;
        }

        if (Boards.Count >= MetricBoardValidation.MaxBoards)
        {
            throw new ArgumentException($"At most {MetricBoardValidation.MaxBoards} metric boards can be seeded.", nameof(name));
        }

        var seed = new MetricBoardSeed { Name = name };
        Boards.Add(seed);

        return seed;
    }
}

/// <summary>
/// Seeds the metrics health boards (#326, §8.34) from code, inside the <c>AddWarp</c> / <c>AddWarpServer</c> lambda:
/// <code>
/// opt.AddMetricBoard(b =&gt; b
///     .Ratio("Payment success", "payment.results", "result", "ok", "declined,error", check: MetricTileCheck.Deviation(0.02, direction: MetricDeviationDirection.Drop))
///     .Value("5xx", "http.server.request.duration", tagFilter: "http.response.status_code=5*", check: MetricTileCheck.AtMost(0)));
///
/// opt.AddMetricBoard("Checkout", b =&gt; b
///     .Ratio("Checkout success", "shop.checkout.completed", "outcome", "ok", "error,declined", check: MetricTileCheck.AtLeast(0.98)));
/// </code>
/// Separate from <c>AddMetrics</c> on purpose: that starts the collector and flusher, which a dashboard-only host has no
/// use for. The board's read/command services and the <c>metric_board</c> / <c>metric_board_tile</c> tables are always
/// present (§2.11) — this only registers the insert-if-absent seeder. Calling it more than once for the same board (by
/// name, ignoring case; the unnamed overload and the name <c>Default</c> are the Default board) appends to that board.
/// </summary>
public static class MetricBoardServiceConfiguration
{
    // The SLO seeder must run before the board seeder: an SLO tile is resolved against the stored objectives. Hosted services
    // start in registration order, so AddSlo moves an already-registered board seeder after its own seeder, and
    // AddMetricBoard appends (TryAddEnumerable) after one registered before it.
    internal static void MoveSeederLast(IServiceCollection services)
    {
        var seeders = services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Where(x => x.ImplementationType is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(MetricBoardSeeder<>))
            .ToList();
        foreach (var seeder in seeders)
        {
            services.Remove(seeder);
            services.Add(seeder);
        }
    }

    /// <summary>Seeds tiles on the Default board, which owns every tile without a board.</summary>
    public static IWarpBuilder AddMetricBoard(this IWarpBuilder builder, Action<MetricBoardOptions> configure)
        => AddMetricBoardCore(builder, null, configure);

    /// <summary>
    /// Seeds the board named <paramref name="name"/> (created when no board has that name, ignoring case) and its tiles.
    /// The name <c>Default</c> is the Default board.
    /// </summary>
    public static IWarpBuilder AddMetricBoard(this IWarpBuilder builder, string name, Action<MetricBoardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(name);

        var board = new MetricBoard { Name = name };
        if (!MetricBoardValidation.TryValidateBoard(board, out var error))
        {
            throw new ArgumentException($"Invalid metric board name '{name}': {error}", nameof(name));
        }

        return AddMetricBoardCore(builder, MetricBoardStore.SameName(board.Name, MetricBoardValidation.DefaultBoardName) ? null : board.Name, configure);
    }

    private static IWarpBuilder AddMetricBoardCore(IWarpBuilder builder, string? name, Action<MetricBoardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var existing = builder.Services
            .Where(x => x.ServiceType == typeof(MetricBoardSeeds))
            .Select(x => x.ImplementationInstance)
            .OfType<MetricBoardSeeds>()
            .FirstOrDefault();
        var seeds = existing ?? new MetricBoardSeeds();
        configure(seeds.For(name).Tiles);

        // The Default board with no tiles seeds nothing (it is created on first use anyway); a named board is seeded even
        // empty, so its tiles can be authored in the dashboard.
        seeds.Boards.RemoveAll(x => x.Name is null && x.Tiles.Seeded.Count == 0);
        if (seeds.Boards.Count == 0)
        {
            return builder;
        }

        if (existing is null)
        {
            builder.Services.AddSingleton(seeds);
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
/// Startup seeder for config-defined health boards and their tiles (#326), the <c>SloSeeder</c> shape: insert-if-absent.
/// A board matches a stored board by its seed key, then by name, ignoring case (the unnamed seed is the Default board,
/// created when missing), so a dashboard rename keeps the seeded board; a
/// tile matches a stored tile ON THE SAME BOARD by name or by fields (<see cref="MetricBoardOptions.SameFields"/>), so a
/// dashboard edit of either survives a restart. Each seed is validated against the registry kind of its instrument (a
/// ratio on a gauge is skipped, logged at Error). The reads, the caps and the inserts run under the transaction-scoped
/// <c>warp:metric-board</c> lock (§2.16), shared with the dashboard's writes: N replicas starting at once would otherwise
/// each find a board missing and each seed a copy. The schema is migrator-gated present, so a failure is a real error,
/// logged at Error and non-fatal (boards and tiles can still be authored in the dashboard).
/// An SLO tile whose objective does not exist YET (another process seeds it, or the host starts hosted services
/// concurrently so the SLO seeder has not run) is retried in the background: every
/// <see cref="MetricBoardSeeds.RetryInterval"/>, at most <see cref="MetricBoardSeeds.MaxRetries"/> times, re-running the
/// pass for those tiles only (insert-if-absent, so a re-run is safe), never blocking startup and cancelled by
/// <see cref="StopAsync"/>. The first miss logs a Warning, only the last an Error. An ambiguous objective (two match) is
/// never retried: it does not resolve on its own.
/// </summary>
internal sealed class MetricBoardSeeder<TContext> : IHostedService, IDisposable
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricBoardSeeds _seeds;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricBoardSeeder<TContext>> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _retry;

    public MetricBoardSeeder(IServiceScopeFactory scopeFactory, MetricBoardSeeds seeds, TimeProvider timeProvider, ILogger<MetricBoardSeeder<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _seeds = seeds;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // Test seam: runs inside the lock after the stored boards and tiles were read and before the tile inserts are saved,
    // so a race test can pin one seeder there (N=2 BarrierSignal) and prove the other cannot read until it commits.
    internal Func<CancellationToken, Task>? BeforeSave { get; init; }

    // Test seam: released after every retry pass, so a test advancing a fake clock knows the pass it triggered is done.
    internal SemaphoreSlim? RetryPassed { get; init; }

    // The background retry, null when none was needed (test seam: completes when every pending tile resolved or gave up).
    internal Task? Retry => _retry;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pending = await SeedPassAsync(_seeds.Boards, retriesLeft: MetricBoardSeeds.MaxRetries, cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        // Created before StartAsync returns, so the first tick comes one interval after startup whatever the scheduling.
        var timer = new PeriodicTimer(MetricBoardSeeds.RetryInterval, _timeProvider);
        _retry = RetryAsync(timer, pending, _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_retry is not null)
        {
            await _retry.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RetryAsync(PeriodicTimer timer, List<MetricBoardSeed> pending, CancellationToken ct)
    {
        using (timer)
        {
            try
            {
                for (var attempt = 1; attempt <= MetricBoardSeeds.MaxRetries && pending.Count > 0; attempt++)
                {
                    if (!await timer.WaitForNextTickAsync(ct))
                    {
                        return;
                    }

                    pending = await SeedPassAsync(pending, MetricBoardSeeds.MaxRetries - attempt, ct);
                    RetryPassed?.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The host is stopping, so the retry ends quietly.
            }
        }
    }

    // One seed pass over the given boards. It returns, per board, the SLO tiles whose objective matched nothing while
    // retries are left, for the background retry; on the last pass those are logged at Error and dropped instead.
    private async Task<List<MetricBoardSeed>> SeedPassAsync(IReadOnlyList<MetricBoardSeed> seeds, int retriesLeft, CancellationToken cancellationToken)
    {
        var pending = new List<MetricBoardSeed>();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricQueryService>();
            var queries = scope.ServiceProvider.GetService<IWarpSqlQueries<TContext>>();

            // SLO tiles name their objective; one read of the objectives resolves every one of them (the SLO seeder ran
            // first: AddSlo orders its seeder before this one).
            var objectives = await ReadObjectivesAsync(context, seeds, cancellationToken);

            // Validated before the lock (one registry read per seed), so the locked section is the board read and the inserts.
            var boards = new List<PreparedBoard>();
            foreach (var board in seeds)
            {
                var tiles = new List<PreparedTile>();
                var missing = new MetricBoardSeed { Name = board.Name };
                foreach (var seed in board.Tiles.Seeded)
                {
                    // Validated first, so the match sees the normalized fields the stored row was written with (a gauge's
                    // default aggregate).
                    var tile = Copy(seed);
                    if (board.Tiles.SloSeeds.TryGetValue(seed, out var objective))
                    {
                        var resolution = TryResolve(tile, objective, objectives, board.Name, retriesLeft);
                        if (resolution == SloResolution.NotFound && retriesLeft > 0)
                        {
                            missing.Tiles.Seeded.Add(seed);
                            missing.Tiles.SloSeeds[seed] = objective;
                        }

                        if (resolution != SloResolution.Resolved)
                        {
                            continue;
                        }
                    }

                    var (ok, error) = await MetricBoardTileValidator.ValidateAsync(tile, metrics, cancellationToken);
                    tiles.Add(new PreparedTile(tile, ok, error));
                }

                boards.Add(new PreparedBoard(board.Name, tiles));
                if (missing.Tiles.Seeded.Count > 0)
                {
                    pending.Add(missing);
                }
            }

            var (boardsAdded, tilesAdded) = await TransactionLock.RunAsync(queries, context, TransactionLock.MetricBoardKey, TransactionLock.SeedTimeout, (x, ct) => SeedAsync(x, boards, ct), cancellationToken);
            if (boardsAdded > 0)
            {
                _logger.LogInformation("Seeded {Count} metric board(s) from config", boardsAdded);
            }

            if (tilesAdded > 0)
            {
                _logger.LogInformation("Seeded {Count} metric board tile(s) from config", tilesAdded);
            }

            return pending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metric board seeding failed — {Count} config-declared tile(s) were NOT seeded and will be missing until created in the dashboard. The schema is expected present (migrator gating), so this is a real error, not a startup race.", seeds.Sum(x => x.Tiles.Seeded.Count));

            return [];
        }
    }

    // The (id, name, application) of every objective, only when an SLO tile is seeded; a handful of rows, matched in memory
    // so a collation never decides whether two names are one.
    private static async Task<List<(int Id, string Name, string? Application)>> ReadObjectivesAsync(TContext context, IReadOnlyList<MetricBoardSeed> seeds, CancellationToken ct)
    {
        if (!seeds.Any(x => x.Tiles.SloSeeds.Count > 0))
        {
            return [];
        }

        var rows = await context.Set<SloDefinition>()
            .AsNoTracking()
            .Select(x =>
                new
                {
                    x.Id,
                    x.Name,
                    x.Application,
                })
            .ToListAsync(ct);

        return rows.ConvertAll(x => (x.Id, x.Name, x.Application));
    }

    // Exactly one objective must match the seed's name (ignoring case) and application (exactly; null matches null). No
    // match may still appear (another process seeds it): a Warning on the first miss, quiet while retrying, an Error once
    // no retry is left. Two matches never resolve on their own: an Error at once.
    private SloResolution TryResolve(MetricBoardTile tile, SloTileSeed seed, List<(int Id, string Name, string? Application)> objectives, string? board, int retriesLeft)
    {
        var matches = objectives.FindAll(x => string.Equals(x.Name, seed.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Application, seed.Application, StringComparison.Ordinal));
        var boardName = board ?? MetricBoardValidation.DefaultBoardName;
        if (matches.Count == 1)
        {
            tile.SloDefinitionId = matches[0].Id;

            return SloResolution.Resolved;
        }

        if (matches.Count > 1 || retriesLeft == 0)
        {
            _logger.LogError(
                "Metric board SLO tile '{Name}' on board '{Board}' was NOT seeded: {Count} objectives match that name and application (exactly one must).",
                tile.Name,
                boardName,
                matches.Count);

            return matches.Count > 1 ? SloResolution.Ambiguous : SloResolution.NotFound;
        }

        if (retriesLeft == MetricBoardSeeds.MaxRetries)
        {
            _logger.LogWarning(
                "Metric board SLO tile '{Name}' on board '{Board}' was not seeded yet: no objective matches that name and application. It may still be seeded by another process; will retry every {Interval} up to {Retries} times.",
                tile.Name,
                boardName,
                MetricBoardSeeds.RetryInterval,
                MetricBoardSeeds.MaxRetries);
        }

        return SloResolution.NotFound;
    }

    // The names of the fields two tiles disagree on, for the skip log: field names only, never a value (§1.2: a filter
    // holds tag values).
    internal static IEnumerable<string> DifferingFields(MetricBoardTile stored, MetricBoardTile seed)
    {
        var fields = new (string Name, bool Same)[]
        {
            (nameof(MetricBoardTile.Instrument), string.Equals(stored.Instrument, seed.Instrument, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.Kind), stored.Kind == seed.Kind),
            (nameof(MetricBoardTile.ChartQuery), string.Equals(stored.ChartQuery, seed.ChartQuery, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.SloDefinitionId), stored.SloDefinitionId == seed.SloDefinitionId),
            (nameof(MetricBoardTile.Application), string.Equals(stored.Application, seed.Application, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.TagFilter), string.Equals(stored.TagFilter, seed.TagFilter, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.RatioTag), string.Equals(stored.RatioTag, seed.RatioTag, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.GoodValues), string.Equals(stored.GoodValues, seed.GoodValues, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.BadValues), string.Equals(stored.BadValues, seed.BadValues, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.GaugeAggregate), stored.GaugeAggregate == seed.GaugeAggregate),
            (nameof(MetricBoardTile.StatusRule), stored.StatusRule == seed.StatusRule),
            (nameof(MetricBoardTile.Comparison), stored.Comparison == seed.Comparison),
            (nameof(MetricBoardTile.Threshold), Nullable.Equals(stored.Threshold, seed.Threshold)),
            (nameof(MetricBoardTile.BaselineDays), stored.BaselineDays == seed.BaselineDays),
            (nameof(MetricBoardTile.MaxDeviation), Nullable.Equals(stored.MaxDeviation, seed.MaxDeviation)),
            (nameof(MetricBoardTile.DeviationDirection), stored.DeviationDirection == seed.DeviationDirection),
        };

        return fields
            .Where(x => !x.Same)
            .Select(x => x.Name);
    }

    private async Task<(int Boards, int Tiles)> SeedAsync(DbContext context, List<PreparedBoard> seeds, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Default first: it owns the null-board tiles, and a new board is placed after it.
        var defaultBoard = await MetricBoardStore.EnsureDefaultAsync(context, now, ct);

        // Tracked: a board matched by name alone is stamped with its seed key below.
        var boards = await context.Set<MetricBoard>()
            .ToListAsync(ct);
        var targets = new List<(int? BoardId, string Name, List<PreparedTile> Tiles)>();
        var created = new List<(MetricBoard Board, List<PreparedTile> Tiles)>();
        var stamped = false;
        foreach (var seed in seeds)
        {
            var match = seed.Name is null ? defaultBoard : MatchSeed(boards, seed.Name);
            if (match is null && seed.Name is not null && MetricBoardStore.SameName(defaultBoard.Name, seed.Name))
            {
                // The Default board was renamed to this seed's name in the dashboard. A named seed never lands on Default
                // (its tiles would be stored with no seed key and seeded again on a board of their own once Default is
                // renamed back), and creating the board would break name uniqueness: skip it, like a renamed seeded board.
                _logger.LogError("Metric board '{Board}' was NOT seeded: the Default board was renamed to that name in the dashboard. Rename one of them.", seed.Name);

                continue;
            }

            if (match is not null && seed.Name is not null && match.SeedKey is not null && !MetricBoardStore.SameName(match.SeedKey, seed.Name))
            {
                // Another seeded board was renamed to this seed's name in the dashboard: names stay unique, so this seed
                // waits until one of the two is renamed.
                _logger.LogError("Metric board '{Board}' was NOT seeded: a board seeded as '{SeedKey}' was renamed to that name in the dashboard. Rename one of them.", seed.Name, match.SeedKey);

                continue;
            }

            if (match is not null)
            {
                // A board created before seed keys existed (or in the dashboard under the seeded name) adopts the seed, so
                // a later dashboard rename still finds it.
                if (seed.Name is not null && match.SeedKey is null && !match.IsDefault)
                {
                    match.SeedKey = seed.Name;
                    stamped = true;
                }

                targets.Add((match.IsDefault ? null : match.Id, match.Name, seed.Tiles));

                continue;
            }

            if (boards.Count >= MetricBoardValidation.MaxBoards)
            {
                _logger.LogError("Metric board '{Board}' was NOT seeded: the host already holds {Max} boards.", seed.Name, MetricBoardValidation.MaxBoards);

                continue;
            }

            var board = new MetricBoard
            {
                Name = seed.Name!,
                SeedKey = seed.Name,
                Position = boards.Max(x => x.Position) + 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.Set<MetricBoard>().Add(board);
            boards.Add(board);
            created.Add((board, seed.Tiles));
        }

        if (created.Count > 0)
        {
            // Flushed inside the lock transaction for the ids the new tiles reference; it commits with them.
            await context.SaveChangesAsync(ct);
            targets.AddRange(created.Select(x => ((int?)x.Board.Id, x.Board.Name, x.Tiles)));
        }

        var stored = await context.Set<MetricBoardTile>()
            .AsNoTracking()
            .ToListAsync(ct);
        var added = 0;
        foreach (var (boardId, name, tiles) in targets)
        {
            var onBoard = boardId is null
                ? stored.FindAll(x => x.BoardId is null || x.BoardId == defaultBoard.Id)
                : stored.FindAll(x => x.BoardId == boardId);
            added += SeedTiles(context, boardId, name, onBoard, tiles, now);
        }

        if (BeforeSave is not null)
        {
            await BeforeSave(ct);
        }

        if (added > 0 || stamped)
        {
            await context.SaveChangesAsync(ct);
        }

        return (created.Count, added);
    }

    // The seed key first, then the name; both ignore case in memory, so a collation never decides whether two boards are
    // one. The seed key is what survives a dashboard rename: a board found by name with no seed key is one the seed has
    // not adopted yet. The Default board is never a named seed's match (only the unnamed seed and `Default` reach it), even
    // after it was renamed to the seed's name.
    private static MetricBoard? MatchSeed(List<MetricBoard> boards, string name)
        => boards.Find(x => x.SeedKey is not null && MetricBoardStore.SameName(x.SeedKey, name))
            ?? boards.Find(x => !x.IsDefault && MetricBoardStore.SameName(x.Name, name));

    private int SeedTiles(DbContext context, int? boardId, string board, List<MetricBoardTile> stored, List<PreparedTile> seeds, DateTime now)
    {
        var position = stored.Count == 0 ? 0 : stored.Max(x => x.Position);
        var added = 0;

        foreach (var (tile, ok, error) in seeds)
        {
            // Matched in memory, ordinally: the board is a handful of rows, and a case-insensitive collation must not make
            // two tiles that differ only by case read as one.
            if (stored.Exists(x => MetricBoardOptions.SameFields(x, tile)))
            {
                continue;
            }

            var named = stored.Find(x => string.Equals(x.Name, tile.Name, StringComparison.Ordinal));
            if (named is not null)
            {
                // The stored tile wins (a dashboard edit survives a restart), but say so: a changed seed that never takes
                // effect is otherwise silent. Information, not Warning: an intended dashboard edit logs this every boot.
                _logger.LogInformation(
                    "Metric board tile '{Name}' ({Kind}) on board '{Board}' was not seeded: a stored tile already has that name and differs in {Fields}. The stored tile is kept; rename the seed or delete the stored tile to apply the config.",
                    tile.Name,
                    tile.Kind,
                    board,
                    string.Join(", ", DifferingFields(named, tile)));

                continue;
            }

            if (!ok)
            {
                _logger.LogError("Metric board tile '{Name}' on board '{Board}' was NOT seeded: {Error}", tile.Name, board, error);

                continue;
            }

            if (stored.Count >= MetricBoardValidation.MaxTiles)
            {
                _logger.LogError("Metric board tile '{Name}' was NOT seeded: board '{Board}' already holds {Max} tiles.", tile.Name, board, MetricBoardValidation.MaxTiles);

                continue;
            }

            if (tile.Kind == MetricTileKind.Chart && stored.Count(x => x.Kind == MetricTileKind.Chart) >= MetricBoardValidation.MaxChartTiles)
            {
                _logger.LogError("Metric board tile '{Name}' was NOT seeded: board '{Board}' already holds {Max} chart tiles.", tile.Name, board, MetricBoardValidation.MaxChartTiles);

                continue;
            }

            tile.BoardId = boardId;
            tile.Position = ++position;
            tile.CreatedAt = now;
            tile.UpdatedAt = now;
            context.Set<MetricBoardTile>().Add(tile);
            stored.Add(tile);
            added++;
        }

        return added;
    }

    // The options keep their own instances: a seed is copied so the tracked entity never aliases the singleton.
    private static MetricBoardTile Copy(MetricBoardTile x) => new()
    {
        Name = x.Name,
        Instrument = x.Instrument,
        Application = x.Application,
        TagFilter = x.TagFilter,
        Kind = x.Kind,
        ChartQuery = x.ChartQuery,
        SloDefinitionId = x.SloDefinitionId,
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

    private enum SloResolution
    {
        Resolved = 1,
        NotFound = 2,
        Ambiguous = 3,
    }

    private sealed record PreparedTile(MetricBoardTile Tile, bool Ok, string? Error);

    private sealed record PreparedBoard(string? Name, List<PreparedTile> Tiles);
}
