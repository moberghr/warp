using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Core.Services;

/// <summary>Creates, edits and deletes metric annotations (#333). Registered by <c>AddWarp</c> so any dashboard host resolves it.</summary>
public interface IMetricAnnotationCommandService
{
    /// <summary>
    /// Insert (Id == 0) or update an annotation; returns its id, or null when the annotation to update does not exist.
    /// <paramref name="author"/> is the acting dashboard user: an insert stores it as the note's author, an update keeps
    /// the original author and records the editor in the audit log only.
    /// </summary>
    /// <remarks>
    /// Persists <paramref name="annotation"/> as given: callers validate (and normalize) it first with
    /// <see cref="MetricAnnotationValidator.ValidateAsync"/>, as the dashboard upsert endpoint does.
    /// </remarks>
    Task<int?> Upsert(MetricAnnotation annotation, string author, CancellationToken ct);

    /// <summary>Deletes an annotation; false when it does not exist. <paramref name="author"/> is the acting user, for the audit log.</summary>
    Task<bool> Delete(int id, string author, CancellationToken ct);
}

/// <summary>
/// <see cref="IMetricAnnotationCommandService"/> over the user's <typeparamref name="TContext"/>: a low-contention
/// optimistic load-set-save with a structured <c>LogInformation</c> audit naming the acting user, mirroring
/// <c>SloCommandService</c> and <c>MetricBoardCommandService</c>. The note text is never logged (§1.2: it is free text).
/// </summary>
public sealed class MetricAnnotationCommandService<TContext> : IMetricAnnotationCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricAnnotationCommandService<TContext>> _logger;

    public MetricAnnotationCommandService(TContext context, TimeProvider timeProvider, ILogger<MetricAnnotationCommandService<TContext>> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int?> Upsert(MetricAnnotation annotation, string author, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        var actor = MetricAnnotationValidation.NormalizeAuthor(author);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (annotation.Id <= 0)
        {
            annotation.Id = 0;
            annotation.Author = actor;
            annotation.CreatedAt = now;
            annotation.UpdatedAt = now;
            _context.Set<MetricAnnotation>().Add(annotation);
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Metric annotation {Id} created by {Author} on {Target} {TargetName}",
                annotation.Id,
                actor,
                annotation.Target,
                TargetName(annotation));

            return annotation.Id;
        }

        var existing = await _context.Set<MetricAnnotation>()
            .Where(x => x.Id == annotation.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            return null;
        }

        // The caller validated, which cleared the fields of the other target, so copying them as they are also clears a
        // field left over from the note's previous target. The author stays the note's creator.
        existing.Target = annotation.Target;
        existing.Instrument = annotation.Instrument;
        existing.TagFilter = annotation.TagFilter;
        existing.SloDefinitionId = annotation.SloDefinitionId;
        existing.Note = annotation.Note;
        existing.StartsAt = annotation.StartsAt;
        existing.EndsAt = annotation.EndsAt;
        existing.AcknowledgedNoise = annotation.AcknowledgedNoise;
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Metric annotation {Id} updated by {Author} on {Target} {TargetName}",
            existing.Id,
            actor,
            existing.Target,
            TargetName(existing));

        return existing.Id;
    }

    public async Task<bool> Delete(int id, string author, CancellationToken ct)
    {
        var annotation = await _context.Set<MetricAnnotation>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);
        if (annotation is null)
        {
            return false;
        }

        _context.Set<MetricAnnotation>().Remove(annotation);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Metric annotation {Id} deleted by {Author} on {Target} {TargetName}",
            id,
            MetricAnnotationValidation.NormalizeAuthor(author),
            annotation.Target,
            TargetName(annotation));

        return true;
    }

    private static string TargetName(MetricAnnotation annotation)
        => annotation.Target == MetricAnnotationTarget.Slo
            ? $"slo:{annotation.SloDefinitionId}"
            : annotation.Instrument ?? string.Empty;
}
