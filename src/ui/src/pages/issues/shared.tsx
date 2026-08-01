// Shared presentational helpers for the Issues pages (list + detail): the source badge (same palette
// as the unified trace waterfall §8.28) and the resolution-status chip.
import { ErrorSource, ErrorGroupStatus } from '@/types/issues';

// job=blue, endpoint=slate/"server", adapter=purple/"outbound", client=green — matches
// TraceWaterfall.tsx SOURCE_STYLES so a span and its issue read with the same colour.
const SOURCE_STYLES: Record<ErrorSource, { label: string; badge: string }> = {
  [ErrorSource.Job]: { label: 'job', badge: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
  [ErrorSource.Endpoint]: { label: 'server', badge: 'bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200' },
  [ErrorSource.Adapter]: { label: 'outbound', badge: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
  [ErrorSource.Client]: { label: 'client', badge: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
};

export function SourceBadge({ source }: { source: ErrorSource }) {
  const s = SOURCE_STYLES[source] ?? { label: 'unknown', badge: 'bg-gray-100 text-gray-700' };

  return <span className={`inline-block rounded px-1.5 py-0.5 text-xs font-medium ${s.badge}`}>{s.label}</span>;
}

const STATUS_STYLES: Record<ErrorGroupStatus, { label: string; cls: string }> = {
  [ErrorGroupStatus.Unresolved]: { label: 'Unresolved', cls: 'bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300' },
  [ErrorGroupStatus.Resolved]: { label: 'Resolved', cls: 'bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300' },
  [ErrorGroupStatus.Ignored]: { label: 'Ignored', cls: 'bg-muted text-muted-foreground' },
};

export function StatusChip({ status }: { status: ErrorGroupStatus }) {
  const s = STATUS_STYLES[status] ?? { label: 'Unknown', cls: 'bg-gray-100 text-gray-700' };

  return <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${s.cls}`}>{s.label}</span>;
}

// "new" (first seen this window) and "regressed" (fired again after being resolved) flags — the two
// signals that make an issue jump the queue. Rendered inline next to the issue title.
export function IssueFlags({ isNew, isRegressed }: { isNew: boolean; isRegressed: boolean }) {
  return (
    <>
      {isNew && (
        <span className="inline-flex items-center rounded-full bg-blue-100 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300 px-1.5 py-0.5 text-[11px] font-medium">
          new
        </span>
      )}
      {isRegressed && (
        <span className="inline-flex items-center rounded-full bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300 px-1.5 py-0.5 text-[11px] font-medium">
          regressed
        </span>
      )}
    </>
  );
}
