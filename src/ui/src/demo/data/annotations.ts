// Demo fixtures for metric annotations (#333): a few known-issue notes on the demo instruments (data/metrics.ts) and one
// on a demo SLO (data/slo.ts), placed relative to "now" so they show on the default ranges. The board fixture attaches
// them to its tiles the way the server does.

import { MetricAnnotationTarget } from '@/types/annotations';
import type { MetricAnnotation, MetricAnnotationList } from '@/types/annotations';
import type { MetricBoardTile } from '@/types/metricBoard';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;

function note(partial: Partial<MetricAnnotation> & Pick<MetricAnnotation, 'id' | 'note'>, now: number): MetricAnnotation {
  const startsAt = partial.startsAt ?? new Date(now - 2 * DAY).toISOString();

  return {
    target: MetricAnnotationTarget.Instrument,
    instrument: null,
    tagFilter: null,
    sloDefinitionId: null,
    author: 'ops@example.com',
    endsAt: null,
    acknowledgedNoise: false,
    createdAt: startsAt,
    updatedAt: startsAt,
    ...partial,
    startsAt,
  };
}

/** Every demo note, relative to `now`. */
export function demoAnnotations(now: number): MetricAnnotation[] {
  return [
    note(
      {
        id: 1,
        note: 'Duplicate provider notification race, about once a day, predates this release',
        instrument: 'http.server.request.duration',
        tagFilter: 'http.response.status_code=5*',
        startsAt: new Date(now - 3 * DAY).toISOString(),
        acknowledgedNoise: true,
      },
      now,
    ),
    note(
      {
        id: 2,
        note: 'Card processor maintenance window',
        instrument: 'payment.results',
        startsAt: new Date(now - 5 * HOUR).toISOString(),
        endsAt: new Date(now - 3 * HOUR).toISOString(),
      },
      now,
    ),
    note(
      {
        id: 3,
        note: 'Email provider rate-limits us during the nightly batch; retries recover',
        target: MetricAnnotationTarget.Slo,
        sloDefinitionId: 1,
        author: 'anonymous',
        startsAt: new Date(now - 7 * DAY).toISOString(),
      },
      now,
    ),
  ];
}

/** GET /annotations?instrument=|slo=&from=&to= — the notes of one target overlapping the window, newest first. */
export function demoAnnotationList(params: { instrument?: string; slo?: string | number; from?: string; to?: string }, now = Date.now()): MetricAnnotationList {
  const from = params.from ? Date.parse(params.from) : Number.NEGATIVE_INFINITY;
  const to = params.to ? Date.parse(params.to) : Number.POSITIVE_INFINITY;
  const slo = params.slo === undefined ? null : Number(params.slo);
  const items = demoAnnotations(now)
    .filter((a) => params.instrument === undefined || (a.target === MetricAnnotationTarget.Instrument && a.instrument === params.instrument))
    .filter((a) => slo === null || (a.target === MetricAnnotationTarget.Slo && a.sloDefinitionId === slo))
    .filter((a) => Date.parse(a.startsAt) < to && (a.endsAt === null || Date.parse(a.endsAt) > from))
    .sort((a, b) => Date.parse(b.startsAt) - Date.parse(a.startsAt));

  return { items, isTruncated: false };
}

/**
 * The notes the board attaches to a demo tile over `[from, to)`: same instrument, and filters that do not contradict
 * (a simplified MetricAnnotationMatch: no filter on either side, or the same filter).
 */
export function demoTileAnnotations(tile: MetricBoardTile, from: number, to: number): MetricAnnotation[] {
  return demoAnnotationList({ instrument: tile.instrument, from: new Date(from).toISOString(), to: new Date(to).toISOString() }).items
    .filter((a) => a.tagFilter === null || tile.tagFilter === null || a.tagFilter === tile.tagFilter);
}
