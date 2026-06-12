import { format } from 'date-fns';
import { DateTime } from 'luxon';
import { State } from '@/types';
import { useSettingsStore } from '@/stores/settings';

// Uses `Date.now()` rather than `new Date()` for the "now" baseline so demo
// mode can pin the clock via a single `Date.now` override and keep "X ago"
// labels stable across screenshot runs. Luxon (already pulled in by
// chartjs-adapter-luxon) replaces date-fns here so the bundle ships a single
// date library instead of two.
export function formatRelativeTime(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString))
    .toRelative({ base: DateTime.fromMillis(Date.now()) }) ?? '';
}

export function formatDateTime(dateString: string | Date, pattern?: string): string {
  const fmt = pattern ?? useSettingsStore.getState().dateFormat;
  const date = typeof dateString === 'string' ? new Date(dateString) : dateString;

  return format(date, fmt);
}

export function formatDateTimeExact(dateString: string | Date, pattern?: string): string {
  return formatDateTime(dateString, pattern);
}

export function shortType(fullType: string | null | undefined): string {
  if (!fullType) return '—';
  const parts = fullType.split(',')[0].split('.');
  return parts[parts.length - 1];
}

const stateNames: Record<number, string> = {
  [State.Enqueued]: 'Enqueued',
  [State.Awaiting]: 'Awaiting',
  [State.Processing]: 'Processing',
  [State.Completed]: 'Completed',
  [State.Failed]: 'Failed',
  [State.Deleted]: 'Deleted',
  [State.Scheduled]: 'Scheduled',
};

export function stateName(state: State): string {
  return stateNames[state] ?? 'Unknown';
}

export function stateColor(state: State): string {
  switch (state) {
    case State.Enqueued: return 'bg-state-enqueued-bg text-state-enqueued border-transparent';
    case State.Awaiting: return 'bg-state-awaiting-bg text-state-awaiting border-transparent';
    case State.Processing: return 'bg-state-processing-bg text-state-processing border-transparent';
    case State.Completed: return 'bg-state-completed-bg text-state-completed border-transparent';
    case State.Failed: return 'bg-state-failed-bg text-state-failed border-transparent';
    case State.Deleted: return 'bg-state-deleted-bg text-state-deleted border-transparent';
    case State.Scheduled: return 'bg-state-scheduled-bg text-state-scheduled border-transparent';
    default: return 'bg-state-deleted-bg text-state-deleted border-transparent';
  }
}

export function shortId(id: string): string {
  return id.substring(0, 8);
}

export function detailPath(id: string, kind?: number | null): string {
  if (kind === 3) {
    return `/batches/detail/${id}`;
  }
  if (kind === 2) {
    return `/messages/detail/${id}`;
  }
  if (kind === 1) {
    return `/jobs/detail/${id}`;
  }

  return `/detail/${id}`;
}

export function formatDuration(ms: number | null | undefined): string | null {
  if (ms == null) return null;
  if (ms < 1) return '<1ms';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  const mins = Math.floor(ms / 60000);
  const secs = ((ms % 60000) / 1000).toFixed(0);

  return `${mins}m ${secs}s`;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(0)} MB`;

  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

const HEARTBEAT_STALE_THRESHOLD_MS = 30_000;

export function isServerStale(lastHeartbeatTime: string): boolean {
  return Date.now() - new Date(lastHeartbeatTime).getTime() > HEARTBEAT_STALE_THRESHOLD_MS;
}

export function serverStatusDotColor(lastHeartbeatTime: string, pausedAt: string | null): string {
  if (pausedAt) {
    return 'bg-amber-500';
  }

  const elapsed = Date.now() - new Date(lastHeartbeatTime).getTime();
  if (elapsed > HEARTBEAT_STALE_THRESHOLD_MS) {
    return 'bg-red-500';
  }

  return 'bg-green-500';
}
