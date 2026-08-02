using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Slo;

/// <summary>Presence marker for the dashboard <c>slo</c> addon flag — registered by <see cref="SloServiceConfiguration.AddSlo"/>.</summary>
public interface ISloMarker;

/// <inheritdoc cref="ISloMarker"/>
public sealed class SloMarker : ISloMarker;

/// <summary>
/// Objectives seeded from config via <c>opt.AddSlo(o =&gt; o.AddObjective(...))</c> (§8.31). Seeding is
/// insert-if-absent (matched on kind + dimension + application + percentile), so a later dashboard edit to the
/// same objective is never clobbered on restart — the DB row wins.
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
        Seeded.Add(new SloDefinition
        {
            Name = name ?? $"{kind} {dimension}",
            Kind = kind,
            Dimension = dimension,
            TargetValue = target,
            WindowSeconds = windowSeconds,
            Percentile = percentile,
            Application = application,
            Enabled = enabled,
        });

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

        var options = new SloOptions();
        configure?.Invoke(options);

        if (options.Seeded.Count > 0)
        {
            var contextType = ResolveContextType(builder);
            builder.Services.AddSingleton(options);
            var seederType = typeof(SloSeeder<>).MakeGenericType(contextType);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), seederType));
        }

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
/// percentile) so it never clobbers a dashboard edit. Runs once at boot after the schema is present (§ migrator
/// gating). A best-effort seed — a transient DB error is logged, not fatal.
/// </summary>
internal sealed class SloSeeder<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SloOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SloSeeder<TContext>> _logger;

    public SloSeeder(IServiceScopeFactory scopeFactory, SloOptions options, TimeProvider timeProvider, ILogger<SloSeeder<TContext>> logger)
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
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var added = 0;

            foreach (var seed in _options.Seeded)
            {
                var exists = await context.Set<SloDefinition>().AnyAsync(
                    x => x.Kind == seed.Kind && x.Dimension == seed.Dimension && x.Application == seed.Application && x.Percentile == seed.Percentile,
                    cancellationToken);

                if (exists)
                {
                    continue;
                }

                seed.CreatedAt = now;
                seed.UpdatedAt = now;
                context.Set<SloDefinition>().Add(seed);
                added++;
            }

            if (added > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Seeded {Count} SLO objective(s) from config", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SLO objective seeding failed; objectives can still be created in the dashboard");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
