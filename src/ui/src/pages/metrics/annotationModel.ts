// Pure model behind metric annotations (#333): chart bands, range text, the note editor's draft, its client-side checks
// (mirroring MetricAnnotationValidation, the server's 400 stays the authority) and the upsert body. No React, no fetch.
import { MetricAnnotationTarget, type MetricAnnotation, type MetricAnnotationUpsert } from '@/types/annotations';
import { validateTagFilter } from '@/pages/slo/sloForm';
import { DASHBOARD_LOCALE } from '@/utils/format';
import { fromLocalInputValue, toLocalInputValue } from './metricModel';

/** The longest note the server accepts (MetricAnnotationValidation.MaxNoteLength). */
export const MAX_NOTE_LENGTH = 500;

/** One shaded band on a chart: the note's range clipped to the window. */
export interface AnnotationBand {
  id: number;
  start: number;
  end: number;
  /** The note has no end: the band runs to the window's right edge. */
  ongoing: boolean;
  acknowledged: boolean;
  label: string;
}

/**
 * The bands to shade on a chart over `window`: every note overlapping it, clipped to its ends, oldest first so a newer
 * band draws over an older one. An ongoing note runs to the window's end; a note entirely outside is left out.
 */
export function annotationBands(annotations: MetricAnnotation[], window: { from: number; to: number }): AnnotationBand[] {
  return annotations
    .map((a) => {
      const start = Date.parse(a.startsAt);
      const end = a.endsAt === null ? Number.POSITIVE_INFINITY : Date.parse(a.endsAt);

      return { a, start, end };
    })
    .filter((x) => Number.isFinite(x.start) && x.start < window.to && x.end > window.from)
    .sort((x, y) => x.start - y.start || x.a.id - y.a.id)
    .map((x) => ({
      id: x.a.id,
      start: Math.max(x.start, window.from),
      end: Math.min(x.end, window.to),
      ongoing: x.a.endsAt === null,
      acknowledged: x.a.acknowledgedNoise,
      label: truncate(x.a.note, 40),
    }));
}

/** Whether the note covers `instant` (started at or before it, not yet ended). */
export function isActiveAt(annotation: Pick<MetricAnnotation, 'startsAt' | 'endsAt'>, instant: number): boolean {
  const start = Date.parse(annotation.startsAt);

  return start <= instant && (annotation.endsAt === null || Date.parse(annotation.endsAt) > instant);
}

/** The range as text: `13 Nov, 10:00 → 13 Nov, 12:00`, or `13 Nov, 10:00 → ongoing`. */
export function formatAnnotationRange(annotation: Pick<MetricAnnotation, 'startsAt' | 'endsAt'>): string {
  const format = (iso: string) =>
    new Date(iso).toLocaleString(DASHBOARD_LOCALE, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

  return `${format(annotation.startsAt)} → ${annotation.endsAt === null ? 'ongoing' : format(annotation.endsAt)}`;
}

/** What a note is about, for a list row: `instrument where filter`, `whole instrument`, or the objective. */
export function describeScope(annotation: Pick<MetricAnnotation, 'target' | 'tagFilter' | 'sloDefinitionId'>): string {
  if (annotation.target === MetricAnnotationTarget.Slo) {
    return 'this objective';
  }

  return annotation.tagFilter ? `where ${annotation.tagFilter}` : 'whole instrument';
}

/** A board tile's tooltip: one line per note, `[ack] note — author (range)`. */
export function tileAnnotationSummary(annotations: MetricAnnotation[]): string {
  return annotations
    .map((a) => `${a.acknowledgedNoise ? '[acknowledged noise] ' : ''}${a.note} — ${a.author} (${formatAnnotationRange(a)})`)
    .join('\n');
}

// ---------------------------------------------------------------------------------------------------------------
// Note editor
// ---------------------------------------------------------------------------------------------------------------

/** The editor's state: the range as `<input type="datetime-local">` values, an empty end meaning ongoing. */
export interface AnnotationDraft {
  id: number;
  target: MetricAnnotationTarget;
  instrument: string | null;
  sloDefinitionId: number | null;
  tagFilter: string;
  note: string;
  from: string;
  to: string;
  acknowledgedNoise: boolean;
}

/** A new note on `target`, starting now and ongoing. */
export function newAnnotationDraft(
  target: { instrument: string; tagFilter?: string | null } | { sloDefinitionId: number },
  now: number,
): AnnotationDraft {
  const isSlo = 'sloDefinitionId' in target;

  return {
    id: 0,
    target: isSlo ? MetricAnnotationTarget.Slo : MetricAnnotationTarget.Instrument,
    instrument: isSlo ? null : target.instrument,
    sloDefinitionId: isSlo ? target.sloDefinitionId : null,
    tagFilter: isSlo ? '' : (target.tagFilter ?? ''),
    note: '',
    from: toLocalInputValue(now),
    to: '',
    acknowledgedNoise: false,
  };
}

/** An existing note in the editor. */
export function draftFromAnnotation(annotation: MetricAnnotation): AnnotationDraft {
  return {
    id: annotation.id,
    target: annotation.target,
    instrument: annotation.instrument,
    sloDefinitionId: annotation.sloDefinitionId,
    tagFilter: annotation.tagFilter ?? '',
    note: annotation.note,
    from: toLocalInputValue(Date.parse(annotation.startsAt)),
    to: annotation.endsAt === null ? '' : toLocalInputValue(Date.parse(annotation.endsAt)),
    acknowledgedNoise: annotation.acknowledgedNoise,
  };
}

/** The first problem with the draft, or null: the server's checks, minus whether the target exists. */
export function validateAnnotationDraft(draft: AnnotationDraft): string | null {
  const note = draft.note.trim();
  if (note.length === 0) {
    return 'Write a note.';
  }

  if (note.length > MAX_NOTE_LENGTH) {
    return `A note is at most ${MAX_NOTE_LENGTH} characters (${note.length} now).`;
  }

  const from = fromLocalInputValue(draft.from);
  if (!Number.isFinite(from)) {
    return 'Pick when the note starts.';
  }

  if (draft.to) {
    const to = fromLocalInputValue(draft.to);
    if (!Number.isFinite(to)) {
      return 'The end is not a valid time.';
    }

    if (to <= from) {
      return 'The end must be after the start (leave it empty for an ongoing note).';
    }
  }

  return draft.target === MetricAnnotationTarget.Instrument ? validateTagFilter(draft.tagFilter, 'Tag filter') : null;
}

/** The POST /api/annotations body for a valid draft (instants as ISO UTC; an SLO note carries no filter or ack flag). */
export function draftToUpsert(draft: AnnotationDraft): MetricAnnotationUpsert {
  const isSlo = draft.target === MetricAnnotationTarget.Slo;
  const filter = draft.tagFilter.trim();

  return {
    id: draft.id,
    target: draft.target,
    instrument: isSlo ? null : draft.instrument,
    tagFilter: isSlo || filter.length === 0 ? null : filter,
    sloDefinitionId: isSlo ? draft.sloDefinitionId : null,
    note: draft.note.trim(),
    startsAt: new Date(fromLocalInputValue(draft.from)).toISOString(),
    endsAt: draft.to ? new Date(fromLocalInputValue(draft.to)).toISOString() : null,
    acknowledgedNoise: !isSlo && draft.acknowledgedNoise,
  };
}

function truncate(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}
