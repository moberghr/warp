import { describe, expect, it } from 'vitest';
import { inTimeZone } from '@/lib/timeZone.testUtils';
import { MetricAnnotationTarget, type MetricAnnotation } from '@/types/annotations';
import {
  MAX_NOTE_LENGTH,
  annotationBands,
  describeScope,
  draftFromAnnotation,
  draftToUpsert,
  formatAnnotationRange,
  isActiveAt,
  newAnnotationDraft,
  tileAnnotationSummary,
  validateAnnotationDraft,
} from './annotationModel';
import { fromLocalInputValue, toLocalInputValue } from './metricModel';

const HOUR = 3_600_000;
const T0 = Date.parse('2026-11-13T10:00:00Z');

function note(patch: Partial<MetricAnnotation> = {}): MetricAnnotation {
  return {
    id: 1,
    target: MetricAnnotationTarget.Instrument,
    instrument: 'http',
    tagFilter: null,
    sloDefinitionId: null,
    note: 'Known race',
    author: 'alice',
    startsAt: new Date(T0).toISOString(),
    endsAt: null,
    acknowledgedNoise: false,
    createdAt: new Date(T0).toISOString(),
    updatedAt: new Date(T0).toISOString(),
    ...patch,
  };
}

describe('annotationBands', () => {
  const window = { from: T0, to: T0 + 6 * HOUR };

  it('clips each note to the window and runs an ongoing one to its end', () => {
    const bands = annotationBands(
      [
        note({ id: 1, startsAt: new Date(T0 - HOUR).toISOString(), endsAt: new Date(T0 + HOUR).toISOString() }),
        note({ id: 2, startsAt: new Date(T0 + 2 * HOUR).toISOString(), acknowledgedNoise: true }),
      ],
      window,
    );

    expect(bands).toEqual([
      { id: 1, start: T0, end: T0 + HOUR, ongoing: false, acknowledged: false, label: 'Known race' },
      { id: 2, start: T0 + 2 * HOUR, end: T0 + 6 * HOUR, ongoing: true, acknowledged: true, label: 'Known race' },
    ]);
  });

  it('leaves out notes entirely outside the window, ended at its start or starting at its end', () => {
    const bands = annotationBands(
      [
        note({ id: 1, startsAt: new Date(T0 - 3 * HOUR).toISOString(), endsAt: new Date(T0).toISOString() }),
        note({ id: 2, startsAt: new Date(T0 + 6 * HOUR).toISOString() }),
        note({ id: 3, startsAt: 'not a date' }),
      ],
      window,
    );

    expect(bands).toEqual([]);
  });

  it('orders bands oldest first and shortens a long label', () => {
    const bands = annotationBands(
      [
        note({ id: 5, startsAt: new Date(T0 + 3 * HOUR).toISOString() }),
        note({ id: 4, startsAt: new Date(T0 + HOUR).toISOString(), note: 'x'.repeat(60) }),
      ],
      window,
    );

    expect(bands.map((x) => x.id)).toEqual([4, 5]);
    expect(bands[0].label).toHaveLength(40);
    expect(bands[0].label.endsWith('…')).toBe(true);
  });
});

describe('isActiveAt', () => {
  it('covers its start and stops at its end; an ongoing note never stops', () => {
    const bounded = note({ endsAt: new Date(T0 + HOUR).toISOString() });

    expect(isActiveAt(bounded, T0)).toBe(true);
    expect(isActiveAt(bounded, T0 + HOUR)).toBe(false);
    expect(isActiveAt(bounded, T0 - 1)).toBe(false);
    expect(isActiveAt(note(), T0 + 1000 * HOUR)).toBe(true);
  });
});

describe('text', () => {
  it('writes the range in the pinned UTC zone, an open one as "ongoing"', () => {
    expect(formatAnnotationRange(note())).toBe('Nov 13, 10:00 AM → ongoing');
    expect(formatAnnotationRange(note({ endsAt: new Date(T0 + 2 * HOUR).toISOString() }))).toBe('Nov 13, 10:00 AM → Nov 13, 12:00 PM');
  });

  it('describes the scope of a note', () => {
    expect(describeScope(note())).toBe('whole instrument');
    expect(describeScope(note({ tagFilter: 'status=5*' }))).toBe('where status=5*');
    expect(describeScope(note({ target: MetricAnnotationTarget.Slo, sloDefinitionId: 3 }))).toBe('this objective');
  });

  it('summarises a tile\'s notes one per line, flagging acknowledged noise', () => {
    const summary = tileAnnotationSummary([note({ acknowledgedNoise: true }), note({ id: 2, note: 'Other', author: 'bob' })]);
    const lines = summary.split('\n');

    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/^\[acknowledged noise\] Known race — alice \(/);
    expect(lines[1]).toMatch(/^Other — bob \(/);
  });
});

describe('draft', () => {
  it('starts a new instrument note now, ongoing, with the page filter', () => {
    const draft = newAnnotationDraft({ instrument: 'http', tagFilter: 'status=5*' }, T0);

    expect(draft).toMatchObject({ id: 0, target: MetricAnnotationTarget.Instrument, instrument: 'http', tagFilter: 'status=5*', to: '', note: '' });
    expect(fromLocalInputValue(draft.from)).toBe(T0);
  });

  it('starts a new SLO note without instrument fields', () => {
    const draft = newAnnotationDraft({ sloDefinitionId: 7 }, T0);

    expect(draft).toMatchObject({ target: MetricAnnotationTarget.Slo, sloDefinitionId: 7, instrument: null, tagFilter: '' });
  });

  it('round-trips a stored note through the editor into the upsert body', () => {
    const stored = note({ id: 9, tagFilter: 'status=500', endsAt: new Date(T0 + HOUR).toISOString(), acknowledgedNoise: true });

    const body = draftToUpsert(draftFromAnnotation(stored));

    expect(body).toEqual({
      id: 9,
      target: MetricAnnotationTarget.Instrument,
      instrument: 'http',
      tagFilter: 'status=500',
      sloDefinitionId: null,
      note: 'Known race',
      startsAt: new Date(T0).toISOString(),
      endsAt: new Date(T0 + HOUR).toISOString(),
      acknowledgedNoise: true,
    });
  });

  it('sends no filter or acknowledged flag on an SLO note, and an empty end as ongoing', () => {
    const draft = { ...newAnnotationDraft({ sloDefinitionId: 2 }, T0), note: '  Maintenance  ', tagFilter: 'a=b', acknowledgedNoise: true };

    expect(draftToUpsert(draft)).toMatchObject({ tagFilter: null, acknowledgedNoise: false, endsAt: null, note: 'Maintenance', instrument: null, sloDefinitionId: 2 });
  });
});

describe('validateAnnotationDraft', () => {
  const valid = { ...newAnnotationDraft({ instrument: 'http' }, T0), note: 'Known race' };

  it('accepts a valid note', () => {
    expect(validateAnnotationDraft(valid)).toBeNull();
  });

  it('requires a note and bounds its length', () => {
    expect(validateAnnotationDraft({ ...valid, note: '   ' })).toMatch(/Write a note/);
    expect(validateAnnotationDraft({ ...valid, note: 'x'.repeat(MAX_NOTE_LENGTH + 1) })).toMatch(/at most 500/);
    expect(validateAnnotationDraft({ ...valid, note: 'x'.repeat(MAX_NOTE_LENGTH) })).toBeNull();
  });

  it('requires a start and an end after it', () => {
    expect(validateAnnotationDraft({ ...valid, from: '' })).toMatch(/starts/);
    expect(validateAnnotationDraft({ ...valid, to: valid.from })).toMatch(/after the start/);
    expect(validateAnnotationDraft({ ...valid, to: toLocalInputValue(T0 + HOUR) })).toBeNull();
  });

  it('checks the tag filter grammar on an instrument note only', () => {
    expect(validateAnnotationDraft({ ...valid, tagFilter: 'status' })).not.toBeNull();
    expect(validateAnnotationDraft({ ...newAnnotationDraft({ sloDefinitionId: 1 }, T0), note: 'n', tagFilter: 'status' })).toBeNull();
  });
});

describe('local time in a DST zone (Europe/Zagreb)', () => {
  inTimeZone('Europe/Zagreb');

  // A note that starts in summer time and ends in winter time, across the 25 Oct 2026 change.
  const summerStart = Date.parse('2026-10-24T22:15:00Z'); // 00:15 CEST
  const winterEnd = Date.parse('2026-10-25T03:45:00Z'); // 04:45 CET

  it('opens a stored note in the editor at the wall-clock times of the zone', () => {
    const draft = draftFromAnnotation(note({ startsAt: new Date(summerStart).toISOString(), endsAt: new Date(winterEnd).toISOString() }));

    expect([draft.from, draft.to]).toEqual(['2026-10-25T00:15', '2026-10-25T04:45']);
  });

  it('round-trips a note across the change through the editor into the same instants', () => {
    const stored = note({ startsAt: new Date(summerStart).toISOString(), endsAt: new Date(winterEnd).toISOString() });

    expect(draftToUpsert(draftFromAnnotation(stored))).toMatchObject({ startsAt: stored.startsAt, endsAt: stored.endsAt });
  });

  it('starts a new note at local now, which goes back to the same instant', () => {
    const now = Date.parse('2026-07-15T09:42:00Z');
    const draft = newAnnotationDraft({ instrument: 'http' }, now);

    expect(draft.from).toBe('2026-07-15T11:42');
    expect(fromLocalInputValue(draft.from)).toBe(now);
    expect(draftToUpsert({ ...draft, note: 'n' }).startsAt).toBe('2026-07-15T09:42:00.000Z');
  });

  it('writes the range in local time', () => {
    expect(formatAnnotationRange(note({ startsAt: new Date(summerStart).toISOString(), endsAt: new Date(winterEnd).toISOString() })))
      .toBe('Oct 25, 12:15 AM → Oct 25, 04:45 AM');
  });
});
