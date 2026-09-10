import { DateTime } from 'luxon';
import { State } from '@/types';

// Uses `Date.now()` rather than `new Date()` for the "now" baseline so demo
// mode can pin the clock via a single `Date.now` override and keep "X ago"
// labels stable across screenshot runs. Luxon (already pulled in by
// chartjs-adapter-luxon) replaces date-fns here so the bundle ships a single
// date library instead of two.
// The dashboard renders identically for every viewer. Every locale-sensitive call site passes this
// rather than defaulting to the host locale — a number, a weekday or a relative label that changes
// per machine makes two people looking at the same deployment see different text, and turns a
// screenshot in a bug report into something that cannot be compared against your own screen.
//
// Nothing here is translated: headers, badges and labels are hardcoded English, so a localised
// "za 10 minuta" or "1.234" was mixed-language output, not localisation. If the dashboard is ever
// really localised, this constant is the seam to make configurable.
export const DASHBOARD_LOCALE = 'en-US';

// `now` is a parameter rather than an internal `Date.now()` so the shared ticker (lib/clockTick) can
// format every label on the page against one instant. Defaulted, so the non-ticking callers are
// unchanged.
export function formatRelativeTime(dateString: string, now: number = Date.now()): string {
  return DateTime.fromJSDate(new Date(dateString))
    .toRelative({ base: DateTime.fromMillis(now), locale: DASHBOARD_LOCALE }) ?? '';
}

// How far past its instant a pending row may sit before the label stops saying "due now". A scheduled
// job becomes eligible on ScheduledJobActivation's cadence (10s by default) and then waits for a
// worker to claim it, so a few seconds past due is the normal path, not a symptom.
export const DUE_GRACE_MS = 30_000;

export type CountdownPhase = 'future' | 'due' | 'overdue';

export function countdownPhase(dateString: string, now: number = Date.now()): CountdownPhase {
  const delta = new Date(dateString).getTime() - now;

  if (delta >= 1_000) {
    return 'future';
  }

  return delta > -DUE_GRACE_MS ? 'due' : 'overdue';
}

// Largest whole unit, hand-rolled rather than Luxon's `toRelative`, because a countdown needs the
// same phrasing on both sides of zero: "in 45 seconds" / "overdue by 45 seconds". Stops at days —
// past that nobody is watching a countdown, and "in 40 days" beats "in 1 month" for a cron preview.
export function formatDurationRough(ms: number): string {
  const seconds = Math.floor(Math.abs(ms) / 1000);
  if (seconds < 60) {
    return plural(seconds, 'second');
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return plural(minutes, 'minute');
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return plural(hours, 'hour');
  }

  return plural(Math.floor(hours / 24), 'day');
}

// The label for an instant something is still *waiting for*: a scheduled job, a retry's next attempt,
// a webhook's next delivery, a recurring job's next firing.
//
// Luxon would happily flip "in 30 seconds" to "30 seconds ago" on its own, and for those surfaces
// that reading is wrong: a past `scheduleTime` means the row is due and waiting on activation plus a
// worker, not that it already ran. "due now" says waiting; "overdue by 5 minutes" says something is
// actually wrong (a stopped scheduler, a drained worker pool) instead of quietly claiming a run.
export function formatCountdown(dateString: string, now: number = Date.now()): string {
  const delta = new Date(dateString).getTime() - now;

  switch (countdownPhase(dateString, now)) {
    case 'future':
      return `in ${formatDurationRough(delta)}`;
    case 'due':
      return 'due now';
    default:
      return `overdue by ${formatDurationRough(delta)}`;
  }
}

export function formatDateTime(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm:ss.SSS');
}

export function formatDateTimeExact(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm:ss.SSS');
}

// Second precision — the default for every surface. Milliseconds are three digits of noise in a
// table column: they widen it, they compete with the relative label beside them, and nobody scans a
// list by fractions of a second. The full instant stays one hover away (see RelativeTime), so the
// precision is not lost, just not spent on the default read.
export function formatDateTimeSecond(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm:ss');
}

// Minute precision for cron-derived instants (recurring next/last execution, firing
// history): a cron occurrence is only ever minute-aligned, so seconds and milliseconds
// are noise on those surfaces. Job/log timestamps keep the exact formatter.
export function formatDateTimeMinute(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm');
}

export type TimePrecision = 'exact' | 'second' | 'minute';

// The absolute half of a timestamp, at whichever precision the surface asked for. Lives here rather
// than in RelativeTime so that component file only exports components (fast-refresh rule).
export function absoluteLabel(dateString: string, precision: TimePrecision = 'second'): string {
  if (precision === 'minute') {
    return formatDateTimeMinute(dateString);
  }

  return precision === 'exact' ? formatDateTimeExact(dateString) : formatDateTimeSecond(dateString);
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
    case State.Enqueued: return 'bg-blue-100 text-blue-800';
    case State.Awaiting: return 'bg-yellow-100 text-yellow-800';
    case State.Processing: return 'bg-purple-100 text-purple-800';
    case State.Completed: return 'bg-green-100 text-green-800';
    case State.Failed: return 'bg-red-100 text-red-800';
    case State.Deleted: return 'bg-gray-100 text-gray-800';
    case State.Scheduled: return 'bg-amber-100 text-amber-800';
    default: return 'bg-gray-100 text-gray-800';
  }
}

export function shortId(id: string): string {
  return id.substring(0, 8);
}

// IANA reason phrases for the status codes that realistically show up in call logs; anything
// unmapped falls back to its class so the hover label never comes up empty.
const httpStatusNames: Record<number, string> = {
  100: 'Continue', 101: 'Switching Protocols',
  200: 'OK', 201: 'Created', 202: 'Accepted', 204: 'No Content', 206: 'Partial Content',
  301: 'Moved Permanently', 302: 'Found', 303: 'See Other', 304: 'Not Modified', 307: 'Temporary Redirect', 308: 'Permanent Redirect',
  400: 'Bad Request', 401: 'Unauthorized', 402: 'Payment Required', 403: 'Forbidden', 404: 'Not Found',
  405: 'Method Not Allowed', 406: 'Not Acceptable', 407: 'Proxy Authentication Required', 408: 'Request Timeout',
  409: 'Conflict', 410: 'Gone', 411: 'Length Required', 412: 'Precondition Failed', 413: 'Content Too Large',
  414: 'URI Too Long', 415: 'Unsupported Media Type', 416: 'Range Not Satisfiable', 417: 'Expectation Failed',
  418: "I'm a Teapot", 421: 'Misdirected Request', 422: 'Unprocessable Content', 423: 'Locked', 424: 'Failed Dependency',
  425: 'Too Early', 426: 'Upgrade Required', 428: 'Precondition Required', 429: 'Too Many Requests',
  431: 'Request Header Fields Too Large', 451: 'Unavailable For Legal Reasons',
  500: 'Internal Server Error', 501: 'Not Implemented', 502: 'Bad Gateway', 503: 'Service Unavailable',
  504: 'Gateway Timeout', 505: 'HTTP Version Not Supported',
};

export function httpStatusName(code: number): string {
  const name = httpStatusNames[code];
  if (name) return name;
  if (code >= 100 && code < 200) return 'Informational';
  if (code >= 200 && code < 300) return 'Success';
  if (code >= 300 && code < 400) return 'Redirection';
  if (code >= 400 && code < 500) return 'Client Error';
  if (code >= 500 && code < 600) return 'Server Error';
  return 'Unknown';
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(0)} MB`;

  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

// Six missed ticks at the default 5s HealthCheckInterval. This is the dashboard's own health
// reading, deliberately tighter than the server-side ApplicationInstanceStaleGrace (2 min) that
// governs when an instance row is swept and an InstanceDown alert fires — "should I worry?" is a
// different question from "should this row be deleted?".
export const HEARTBEAT_STALE_THRESHOLD_MS = 30_000;

// `now` is a parameter for the same reason formatRelativeTime takes one: the shared ticker
// (lib/clockTick) re-evaluates staleness against one instant, so a dot goes red on its own rather
// than waiting for the next refetch to re-render it.
export function isServerStale(lastHeartbeatTime: string, now: number = Date.now()): boolean {
  return now - new Date(lastHeartbeatTime).getTime() > HEARTBEAT_STALE_THRESHOLD_MS;
}

export function serverStatusDotColor(lastHeartbeatTime: string, pausedAt: string | null, now: number = Date.now()): string {
  if (pausedAt) {
    return 'bg-amber-500';
  }

  if (isServerStale(lastHeartbeatTime, now)) {
    return 'bg-red-500';
  }

  return 'bg-green-500';
}

function plural(value: number, unit: string): string {
  return `${value} ${unit}${value === 1 ? '' : 's'}`;
}
