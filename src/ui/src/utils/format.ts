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

export function formatRelativeTime(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString))
    .toRelative({ base: DateTime.fromMillis(Date.now()), locale: DASHBOARD_LOCALE }) ?? '';
}

export function formatDateTime(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm:ss.SSS');
}

export function formatDateTimeExact(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm:ss.SSS');
}

// Minute precision for cron-derived instants (recurring next/last execution, firing
// history): a cron occurrence is only ever minute-aligned, so seconds and milliseconds
// are noise on those surfaces. Job/log timestamps keep the exact formatter.
export function formatDateTimeMinute(dateString: string): string {
  return DateTime.fromJSDate(new Date(dateString)).toFormat('yyyy-MM-dd HH:mm');
}

export type TimePrecision = 'exact' | 'minute';

// The absolute half of a timestamp, at whichever precision the surface asked for. Lives here rather
// than in RelativeTime so that component file only exports components (fast-refresh rule).
export function absoluteLabel(dateString: string, precision: TimePrecision = 'exact'): string {
  return precision === 'minute' ? formatDateTimeMinute(dateString) : formatDateTimeExact(dateString);
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
