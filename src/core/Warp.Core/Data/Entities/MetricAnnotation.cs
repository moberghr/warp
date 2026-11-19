using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// A known-issue note (#333, §8.34) an operator attaches to an instrument (optionally with a tag filter) or to an SLO
/// objective, over a time range or open-ended ("ongoing", no end). Shown as a band on the metric chart, on the health
/// board tiles it applies to and on the SLO detail page. Stores only what the operator typed plus the author taken from
/// the authenticated dashboard user (§1.2): no tag values, no payloads. Always-in-schema (§2.11), mirrored by
/// <c>WarpServerContext</c> (§2.14). Kept forever: annotations are tiny and nothing sweeps them; deleting the SLO an
/// annotation targets deletes the annotation with it.
/// </summary>
public class MetricAnnotation
{
    public int Id { get; set; }

    public MetricAnnotationTarget Target { get; set; } = MetricAnnotationTarget.Instrument;

    /// <summary><see cref="MetricAnnotationTarget.Instrument"/>: the instrument name, as in <c>metric_instrument</c>.</summary>
    public string? Instrument { get; set; }

    /// <summary><see cref="MetricAnnotationTarget.Instrument"/>: an optional tag filter in the one <c>MetricFilter</c> grammar.</summary>
    public string? TagFilter { get; set; }

    /// <summary><see cref="MetricAnnotationTarget.Slo"/>: the objective's id (a soft reference; deleting the objective deletes the note).</summary>
    public int? SloDefinitionId { get; set; }

    /// <summary>The note text, at most <c>MetricAnnotationValidation.MaxNoteLength</c> characters.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>The dashboard user who created the note (their identity name), or <c>anonymous</c> on an open dashboard.</summary>
    public string Author { get; set; } = string.Empty;

    public DateTime StartsAt { get; set; }

    /// <summary>The end of the range (exclusive); null = ongoing.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>
    /// Instrument notes only: the noise is known and accepted, so a health-board tile it applies to reads
    /// <c>SloState.Acknowledged</c> instead of Warning / Breaching while the note is active at the window's end.
    /// </summary>
    public bool AcknowledgedNoise { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
