using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads metric annotations (#333). Registered by <c>AddWarp</c> itself, so a dashboard-only host serves them. Read-only.
/// </summary>
public interface IMetricAnnotationQueryService
{
    /// <summary>
    /// The annotations matching <paramref name="query"/>, newest start first, at most
    /// <see cref="MetricAnnotationValidation.MaxPerRead"/> (<see cref="MetricAnnotationListModel.IsTruncated"/> says when
    /// more matched). Throws <see cref="MetricQueryValidationException"/> when both targets are set or the window is
    /// empty or inverted.
    /// </summary>
    Task<MetricAnnotationListModel> GetAnnotations(MetricAnnotationQuery query, CancellationToken ct);
}

/// <summary><see cref="IMetricAnnotationQueryService"/> over the user's <typeparamref name="TContext"/> (§2.14 stays-on-TContext).</summary>
public sealed class MetricAnnotationQueryService<TContext> : IMetricAnnotationQueryService
    where TContext : DbContext
{
    private readonly TContext _context;

    public MetricAnnotationQueryService(TContext context)
    {
        _context = context;
    }

    public async Task<MetricAnnotationListModel> GetAnnotations(MetricAnnotationQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!string.IsNullOrEmpty(query.Instrument) && query.SloDefinitionId is not null)
        {
            throw new MetricQueryValidationException("Read the annotations of an instrument or of an SLO objective, not both.", nameof(query));
        }

        var from = query.From is { } f ? MetricSeriesMath.AsUtc(f) : (DateTime?)null;
        var to = query.To is { } t ? MetricSeriesMath.AsUtc(t) : (DateTime?)null;
        if (from >= to)
        {
            throw new MetricQueryValidationException("The range must have From before To.", nameof(query));
        }

        var annotations = _context.Set<MetricAnnotation>().AsNoTracking();
        if (!string.IsNullOrEmpty(query.Instrument))
        {
            annotations = annotations
                .Where(x => x.Target == MetricAnnotationTarget.Instrument)
                .Where(x => x.Instrument == query.Instrument);
        }

        if (query.SloDefinitionId is { } sloId)
        {
            annotations = annotations
                .Where(x => x.Target == MetricAnnotationTarget.Slo)
                .Where(x => x.SloDefinitionId == sloId);
        }

        annotations = MetricAnnotationQueries.InWindow(annotations, from, to);

        var rows = await annotations
            .OrderByDescending(x => x.StartsAt)
            .ThenByDescending(x => x.Id)
            .Take(MetricAnnotationValidation.MaxPerRead + 1)
            .Select(MetricAnnotationQueries.Projection)
            .ToListAsync(ct);

        return new MetricAnnotationListModel
        {
            Items = rows.Count > MetricAnnotationValidation.MaxPerRead ? rows[..MetricAnnotationValidation.MaxPerRead] : rows,
            IsTruncated = rows.Count > MetricAnnotationValidation.MaxPerRead,
        };
    }
}
