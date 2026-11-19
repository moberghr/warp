using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Core.Slo;

namespace Warp.Core.Metrics;

/// <summary>
/// Boundary validation for a <see cref="MetricAnnotation"/> (#333): the note is bounded, the target is well formed and
/// the range is ordered. On success the annotation is normalized: the note and filter are trimmed, fields of the other
/// target are cleared (an SLO note never carries an instrument, a filter or the acknowledged-noise flag) and the
/// instants are UTC. Whether the instrument is registered or the objective exists is <see cref="MetricAnnotationValidator"/>'s job.
/// </summary>
public static class MetricAnnotationValidation
{
    /// <summary>The longest note, in characters.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>The longest stored author name; a longer identity name is cut.</summary>
    public const int MaxAuthorLength = 200;

    /// <summary>The most annotations one read returns (newest start first); the rest are reported as truncated.</summary>
    public const int MaxPerRead = 200;

    /// <summary>The author of a note written on a dashboard without an authenticated user (open access, §1.5).</summary>
    public const string AnonymousAuthor = "anonymous";

    /// <summary>The stored author for a dashboard user's identity name: trimmed and bounded, or <see cref="AnonymousAuthor"/>.</summary>
    public static string NormalizeAuthor(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return AnonymousAuthor;
        }

        return trimmed.Length > MaxAuthorLength ? trimmed[..MaxAuthorLength] : trimmed;
    }

    /// <summary>Validates and normalizes <paramref name="annotation"/> in place; on failure the error is a caller-facing message.</summary>
    public static bool TryValidate(MetricAnnotation annotation, out string? error)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        annotation.Note = annotation.Note?.Trim() ?? string.Empty;
        if (annotation.Note.Length == 0 || annotation.Note.Length > MaxNoteLength)
        {
            error = $"Note is required and must be at most {MaxNoteLength} characters.";

            return false;
        }

        if (!TryValidateTarget(annotation, out error))
        {
            return false;
        }

        return TryValidateRange(annotation, out error);
    }

    private static bool TryValidateTarget(MetricAnnotation annotation, out string? error)
    {
        if (annotation.Target == MetricAnnotationTarget.Slo)
        {
            if (annotation.SloDefinitionId is not > 0)
            {
                error = "An SLO annotation needs the objective's id (SloDefinitionId).";

                return false;
            }

            annotation.Instrument = null;
            annotation.TagFilter = null;
            annotation.AcknowledgedNoise = false;
            error = null;

            return true;
        }

        if (annotation.Target != MetricAnnotationTarget.Instrument)
        {
            error = $"Target '{(int)annotation.Target}' is not valid: use Instrument (1) or Slo (2).";

            return false;
        }

        annotation.SloDefinitionId = null;
        annotation.Instrument = annotation.Instrument?.Trim();
        if (string.IsNullOrEmpty(annotation.Instrument) || annotation.Instrument.Length > 128)
        {
            error = "Instrument is required and must be at most 128 characters.";

            return false;
        }

        annotation.TagFilter = string.IsNullOrWhiteSpace(annotation.TagFilter) ? null : annotation.TagFilter.Trim();
        if (annotation.TagFilter?.Length > 512)
        {
            error = "TagFilter must be at most 512 characters.";

            return false;
        }

        if (!MetricFilter.TryParse(annotation.TagFilter, out _, out var filterError))
        {
            error = $"TagFilter is invalid: {filterError}";

            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidateRange(MetricAnnotation annotation, out string? error)
    {
        if (annotation.StartsAt == default)
        {
            error = "StartsAt is required.";

            return false;
        }

        annotation.StartsAt = MetricSeriesMath.AsUtc(annotation.StartsAt);
        annotation.EndsAt = annotation.EndsAt is { } end ? MetricSeriesMath.AsUtc(end) : null;
        if (annotation.StartsAt < IMetricQueryService.EarliestFrom || annotation.EndsAt > IMetricQueryService.LatestTo)
        {
            error = "The range is too close to the ends of the calendar.";

            return false;
        }

        if (annotation.EndsAt <= annotation.StartsAt)
        {
            error = "EndsAt must be after StartsAt (leave it empty for an ongoing note).";

            return false;
        }

        error = null;

        return true;
    }
}

/// <summary>
/// Validates a <see cref="MetricAnnotation"/> against what it points at: the instrument must be in the
/// <c>metric_instrument</c> registry, the SLO objective must exist. Shared by the dashboard upsert route.
/// </summary>
public static class MetricAnnotationValidator
{
    public static async Task<(bool Ok, string? Error)> ValidateAsync(
        MetricAnnotation annotation,
        IMetricQueryService metrics,
        ISloQueryService slos,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(slos);

        if (!MetricAnnotationValidation.TryValidate(annotation, out var error))
        {
            return (false, error);
        }

        if (annotation.Target == MetricAnnotationTarget.Slo)
        {
            return await slos.GetObjective(annotation.SloDefinitionId!.Value, ct) is null
                ? (false, $"SLO objective {annotation.SloDefinitionId} does not exist.")
                : (true, null);
        }

        return await metrics.GetInstrumentKindAsync(annotation.Instrument!, ct) is null
            ? (false, $"Instrument '{annotation.Instrument}' is not registered: only collected instruments can be annotated.")
            : (true, null);
    }
}
