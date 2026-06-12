/**
 * Shared style tokens for status-driven UI. Pages should import from here
 * rather than redefining tone maps locally.
 */

export type StateToneKey =
  | 'enqueued'
  | 'scheduled'
  | 'processing'
  | 'completed'
  | 'failed'
  | 'awaiting'
  | 'deleted';

export type StateTone = {
  text: string;
  bg: string;
};

export const STATE_TONE: Record<StateToneKey, StateTone> = {
  enqueued:   { text: 'text-state-enqueued',   bg: 'bg-state-enqueued-bg' },
  scheduled:  { text: 'text-state-scheduled',  bg: 'bg-state-scheduled-bg' },
  processing: { text: 'text-state-processing', bg: 'bg-state-processing-bg' },
  completed:  { text: 'text-state-completed',  bg: 'bg-state-completed-bg' },
  failed:     { text: 'text-state-failed',     bg: 'bg-state-failed-bg' },
  awaiting:   { text: 'text-state-awaiting',   bg: 'bg-state-awaiting-bg' },
  deleted:    { text: 'text-state-deleted',    bg: 'bg-state-deleted-bg' },
};

export function getStateTone(key: string | null | undefined): StateTone {
  if (key && key in STATE_TONE) {
    return STATE_TONE[key as StateToneKey];
  }

  return STATE_TONE.enqueued;
}
