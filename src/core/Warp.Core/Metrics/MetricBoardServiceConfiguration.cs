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
/// </summary>
internal sealed class MetricBoardSeeder<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetricBoardSeeds _seeds;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricBoardSeeder<TContext>> _logger;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricQueryService>();
            var queries = scope.ServiceProvider.GetService<IWarpSqlQueries<TContext>>();

            // Validated before the lock (one registry read per seed), so the locked section is the board read and the inserts.
            var boards = new List<PreparedBoard>();
            foreach (var board in _seeds.Boards)
            {
                var tiles = new List<PreparedTile>();
                foreach (var seed in board.Tiles.Seeded)
                {
                    // Validated first, so the match sees the normalized fields the stored row was written with (a gauge's
                    // default aggregate).
                    var tile = Copy(seed);
                    var (ok, error) = await MetricBoardTileValidator.ValidateAsync(tile, metrics, cancellationToken);
                    tiles.Add(new PreparedTile(tile, ok, error));
                }

                boards.Add(new PreparedBoard(board.Name, tiles));
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metric board seeding failed — {Count} config-declared tile(s) were NOT seeded and will be missing until created in the dashboard. The schema is expected present (migrator gating), so this is a real error, not a startup race.", _seeds.Boards.Sum(x => x.Tiles.Seeded.Count));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The names of the fields two tiles disagree on, for the skip log: field names only, never a value (§1.2: a filter
    // holds tag values).
    internal static IEnumerable<string> DifferingFields(MetricBoardTile stored, MetricBoardTile seed)
    {
        var fields = new (string Name, bool Same)[]
        {
            (nameof(MetricBoardTile.Instrument), string.Equals(stored.Instrument, seed.Instrument, StringComparison.Ordinal)),
            (nameof(MetricBoardTile.Kind), stored.Kind == seed.Kind),
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

    private sealed record PreparedTile(MetricBoardTile Tile, bool Ok, string? Error);

    private sealed record PreparedBoard(string? Name, List<PreparedTile> Tiles);
}
