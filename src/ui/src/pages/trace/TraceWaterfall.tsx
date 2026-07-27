import { useMemo } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { Card, CardContent } from '@/components/ui/card';
import * as api from '@/api';
import type { TraceSpan, TraceSpanSource } from '@/types/trace';

// The unified trace waterfall (§8.28): everything for a trace id — browser request, server endpoint call, the
// jobs it spawned, and the outbound calls those jobs made — on one time axis. Built from the rows Warp already
// persists (no span store); each row links to its own detail where one exists.
export default function TraceWaterfall({ traceId }: { traceId: string }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['trace', 'overview', traceId] as const,
    queryFn: () => api.getTrace(traceId),
    retry: false,
  });

  const rows = useMemo(() => (data ? layout(data.spans) : []), [data]);

  if (isLoading) {
    return <div className="text-sm text-muted-foreground mb-4">Loading trace…</div>;
  }
  // A 404 is the expected "no unified spans for this id" case — render nothing so the job graph below still
  // shows. Any other failure (500 / network) must be visible, not a blank section on a diagnostics screen.
  if (isError && !(isAxiosError(error) && error.response?.status === 404)) {
    return (
      <div className="mb-4 rounded border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
        Couldn't load the trace waterfall.
      </div>
    );
  }
  if (!data || data.spans.length === 0) {
    return null;
  }

  return (
    <div className="mb-6">
      <div className="flex items-center gap-3 mb-2">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase">Trace waterfall</h2>
        <span className="text-xs text-muted-foreground">
          {data.clientCount} client · {data.endpointCount} endpoint · {data.jobCount} job · {data.adapterCount} outbound
          {data.errorCount > 0 && <span className="text-destructive"> · {data.errorCount} error</span>}
          {data.isTruncated && <span className="text-amber-600 dark:text-amber-400"> · showing first {data.spans.length} (truncated)</span>}
        </span>
      </div>
      <Card>
        <CardContent className="p-2 divide-y">
          {rows.map((r) => (
            <WaterfallRow key={`${r.span.source}-${r.span.id}`} row={r} />
          ))}
        </CardContent>
      </Card>
    </div>
  );
}

interface Row {
  span: TraceSpan;
  depth: number;
  leftPct: number;
  widthPct: number;
}

function layout(spans: TraceSpan[]): Row[] {
  const starts = spans.map((s) => new Date(s.startTime).getTime());
  const ends = spans.map((s, i) => starts[i] + (s.durationMs ?? 0));
  const min = Math.min(...starts);
  const total = Math.max(1, Math.max(...ends) - min);

  const byId = new Map(spans.map((s) => [s.id, s]));
  const depthOf = (s: TraceSpan): number => {
    let d = 0;
    let cur: TraceSpan | undefined = s;
    // Only jobs carry a parent link (SpawnedByJobId); walk it, guarding against cycles.
    while (cur?.parentId && byId.has(cur.parentId) && d < 20) {
      d++;
      cur = byId.get(cur.parentId);
    }
    return d;
  };

  return spans.map((span, i) => ({
    span,
    depth: depthOf(span),
    leftPct: ((starts[i] - min) / total) * 100,
    widthPct: Math.max(1.5, ((span.durationMs ?? 0) / total) * 100),
  }));
}

const SOURCE_STYLES: Record<TraceSpanSource, { label: string; badge: string; bar: string }> = {
  client: { label: 'client', badge: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300', bar: 'bg-green-400' },
  endpoint: { label: 'server', badge: 'bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200', bar: 'bg-slate-400' },
  job: { label: 'job', badge: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300', bar: 'bg-blue-400' },
  adapter: { label: 'outbound', badge: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300', bar: 'bg-purple-400' },
};

function WaterfallRow({ row }: { row: Row }) {
  const { span, depth, leftPct, widthPct } = row;
  const s = SOURCE_STYLES[span.source];
  const detail = detailLink(span);
  const name = <span className="font-mono text-xs truncate">{shortName(span)}</span>;

  return (
    <div className="flex items-center gap-2 px-2 py-1.5 text-sm">
      <div className="w-16 shrink-0">
        <span className={`inline-block rounded px-1.5 py-0.5 text-xs font-medium ${s.badge}`}>{s.label}</span>
      </div>
      <div className="w-64 shrink-0 min-w-0 truncate" style={{ paddingLeft: `${depth * 14}px` }}>
        {detail ? <Link to={detail} className="text-primary hover:underline">{name}</Link> : name}
      </div>
      <div className="relative flex-1 h-4">
        <div
          className={`absolute top-0.5 h-3 rounded ${span.isError ? 'bg-destructive' : s.bar}`}
          style={{ left: `${leftPct}%`, width: `${widthPct}%` }}
          title={`${span.status}${span.durationMs != null ? ` · ${Math.round(span.durationMs)}ms` : ''}`}
        />
      </div>
      <div className="w-20 shrink-0 text-right text-xs tabular-nums text-muted-foreground">
        {span.durationMs != null ? `${Math.round(span.durationMs)}ms` : '—'}
      </div>
    </div>
  );
}

function shortName(span: TraceSpan): string {
  // Job types are assembly-qualified; show the short type name.
  if (span.source === 'job') {
    return span.name.split(',')[0].split('.').pop() ?? span.name;
  }
  return span.name;
}

function detailLink(span: TraceSpan): string | null {
  if (span.source === 'job') return `/detail/${span.id}`;
  if (span.source === 'client') return `/client/events/${span.id}`;
  return null;
}
