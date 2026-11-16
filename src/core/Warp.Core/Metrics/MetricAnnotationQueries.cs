using System.Linq.Expressions;
using Warp.Core.Data.Entities;
using Warp.Core.Models;

namespace Warp.Core.Metrics;

// The shared pieces of every annotation read (#333): the annotation query service and the health board's batched read.
internal static class MetricAnnotationQueries
{
    public static Expression<Func<MetricAnnotation, MetricAnnotationModel>> Projection { get; } = x =>
        new MetricAnnotationModel
        {
            Id = x.Id,
            Target = x.Target,
            Instrument = x.Instrument,
            TagFilter = x.TagFilter,
            SloDefinitionId = x.SloDefinitionId,
            Note = x.Note,
            Author = x.Author,
            StartsAt = x.StartsAt,
            EndsAt = x.EndsAt,
            AcknowledgedNoise = x.AcknowledgedNoise,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt,
        };

    // [StartsAt, EndsAt) overlapping [from, to), either end open; an ongoing note (no EndsAt) never ends.
    public static IQueryable<MetricAnnotation> InWindow(IQueryable<MetricAnnotation> annotations, DateTime? from, DateTime? to)
    {
        if (to is { } end)
        {
            annotations = annotations.Where(x => x.StartsAt < end);
        }

        if (from is { } start)
        {
            annotations = annotations.Where(x => x.EndsAt == null || x.EndsAt > start);
        }

        return annotations;
    }
}
