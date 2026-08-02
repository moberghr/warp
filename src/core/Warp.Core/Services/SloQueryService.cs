using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>Reads SLO objectives + their rolling evaluations for the dashboard (§8.31). Registered by <c>AddWarp</c>.</summary>
public interface ISloQueryService
{
    Task<SloListModel> GetObjectives(CancellationToken ct);

    Task<SloObjectiveModel?> GetObjective(int id, CancellationToken ct);
}

/// <summary>
/// <see cref="ISloQueryService"/> over the user's <typeparamref name="TContext"/> (§8.31). Joins each durable
/// <see cref="SloDefinition"/> to its one-to-one <see cref="SloEvaluation"/> status row (which the
/// <c>SloEvaluator</c> upserts each tick). Registered by <c>AddWarp</c> itself so any dashboard host resolves it.
/// </summary>
public sealed class SloQueryService<TContext> : ISloQueryService
    where TContext : DbContext
{
    private readonly TContext _context;

    public SloQueryService(TContext context) => _context = context;

    public async Task<SloListModel> GetObjectives(CancellationToken ct)
    {
        var definitions = await _context.Set<SloDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var evaluations = await _context.Set<SloEvaluation>().AsNoTracking().ToDictionaryAsync(x => x.SloDefinitionId, ct);

        return new SloListModel
        {
            Items = definitions.ConvertAll(d => Project(d, evaluations.GetValueOrDefault(d.Id))),
        };
    }

    public async Task<SloObjectiveModel?> GetObjective(int id, CancellationToken ct)
    {
        var definition = await _context.Set<SloDefinition>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (definition is null)
        {
            return null;
        }

        var evaluation = await _context.Set<SloEvaluation>().AsNoTracking().FirstOrDefaultAsync(x => x.SloDefinitionId == id, ct);

        return Project(definition, evaluation);
    }

    private static SloObjectiveModel Project(SloDefinition d, SloEvaluation? e) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Kind = d.Kind,
        Dimension = d.Dimension,
        Application = d.Application,
        TargetValue = d.TargetValue,
        Percentile = d.Percentile,
        WindowSeconds = d.WindowSeconds,
        Enabled = d.Enabled,
        Evaluated = e is not null,
        Attainment = e?.Attainment ?? 0,
        BudgetRemaining = e?.BudgetRemaining ?? 1.0,
        BurnRateShort = e?.BurnRateShort ?? 0,
        BurnRateLong = e?.BurnRateLong ?? 0,
        State = e?.State ?? SloState.Healthy,
        AcknowledgedUntil = e?.AcknowledgedUntil,
        LastEvaluatedAt = e?.LastEvaluatedAt,
    };
}
