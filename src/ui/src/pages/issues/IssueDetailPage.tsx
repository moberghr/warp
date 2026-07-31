import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import { ErrorGroupStatus } from '@/types/issues';
import type { ErrorGroupTrendPoint } from '@/types/issues';
import { SourceBadge, StatusChip, IssueFlags } from './shared';

// Detail for one issue (error group, §8.29): the grouped identity, the most recent captured sample,
// an hourly volume trend, a jump to a representative trace, and the resolve/ignore workflow — the
// drill-down from the Issues list.
export default function IssueDetailPage() {
  const { fingerprint: rawFp } = useParams<{ fingerprint: string }>();
  const fingerprint = useMemo(() => {
    if (!rawFp) return '';
    try { return decodeURIComponent(rawFp); } catch { return rawFp; }
  }, [rawFp]);

  const queryClient = useQueryClient();
  const { data, isLoading, isError } = useQuery({
    queryKey: ['issues', 'detail', fingerprint] as const,
    queryFn: () => api.getIssue(fingerprint),
  });

  const mutation = useMutation({
    mutationFn: (status: ErrorGroupStatus) => api.setIssueStatus(fingerprint, status),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['issues'] });
    },
  });

  if (isError) return <ErrorState message="Issue not found or it has been cleaned up" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="mb-4">
        <Link to="/issues" className="text-sm text-primary hover:underline">← Issues</Link>
        <div className="flex flex-wrap items-center gap-3 mt-1">
          <h1 className="text-2xl font-bold font-mono">{data.exceptionType}</h1>
          <SourceBadge source={data.source} />
          <StatusChip status={data.status} />
          <IssueFlags isNew={data.isNew} isRegressed={data.isRegressed} />
        </div>
        <p className="text-sm text-muted-foreground mt-1">{data.title}</p>
      </div>

      <div className="flex flex-wrap items-center gap-2 mb-4">
        <button
          type="button"
          disabled={mutation.isPending || data.status === ErrorGroupStatus.Resolved}
          onClick={() => mutation.mutate(ErrorGroupStatus.Resolved)}
          className="inline-flex items-center rounded-md bg-primary text-primary-foreground px-3 py-1.5 text-sm font-medium hover:opacity-90 disabled:opacity-40"
        >
          Resolve
        </button>
        <button
          type="button"
          disabled={mutation.isPending || data.status === ErrorGroupStatus.Ignored}
          onClick={() => mutation.mutate(ErrorGroupStatus.Ignored)}
          className="inline-flex items-center rounded-md border px-3 py-1.5 text-sm font-medium hover:bg-accent disabled:opacity-40"
        >
          Ignore
        </button>
        {data.status !== ErrorGroupStatus.Unresolved && (
          <button
            type="button"
            disabled={mutation.isPending}
            onClick={() => mutation.mutate(ErrorGroupStatus.Unresolved)}
            className="inline-flex items-center rounded-md border px-3 py-1.5 text-sm font-medium hover:bg-accent disabled:opacity-40"
          >
            Reopen
          </button>
        )}
        {data.sampleTraceId && (
          <Link
            to={`/trace/${data.sampleTraceId}`}
            className="inline-flex items-center rounded-md border px-3 py-1.5 text-sm font-medium text-primary hover:bg-accent ml-auto"
          >
            Jump to trace →
          </Link>
        )}
      </div>

      <Card className="mb-4">
        <CardContent className="p-4 grid grid-cols-1 md:grid-cols-2 gap-x-8 gap-y-2 text-sm">
          <Field label="First seen" value={data.firstSeenAt} relative />
          <Field label="Last seen" value={data.lastSeenAt} relative />
          <Field label="Events" value={data.count.toLocaleString()} />
          <Field label="Status code" value={data.statusCode != null ? String(data.statusCode) : null} />
          <Field label="Culprit" value={data.culprit} />
          <Field label="Application" value={data.application} />
          <div className="flex gap-2 md:col-span-2">
            <span className="text-muted-foreground w-28 shrink-0">Fingerprint</span>
            <span className="font-mono break-all">{data.fingerprint}</span>
          </div>
        </CardContent>
      </Card>

      {data.trend.length > 0 && (
        <div className="mb-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-2">Events over time</h2>
          <Card>
            <CardContent className="p-3">
              <TrendChart points={data.trend} />
            </CardContent>
          </Card>
        </div>
      )}

      {data.lastSample && (
        <div className="mb-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-2">Latest sample</h2>
          <Card>
            <CardContent className="p-3">
              <pre className="whitespace-pre-wrap break-all text-sm font-mono">{data.lastSample}</pre>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}

function Field({ label, value, relative }: { label: string; value: string | null | undefined; relative?: boolean }) {
  return (
    <div className="flex gap-2">
      <span className="text-muted-foreground w-28 shrink-0">{label}</span>
      {value ? (relative ? <RelativeTime date={value} /> : <span className="break-all">{value}</span>) : <span>—</span>}
    </div>
  );
}

// Dependency-free hourly volume bars. Mirrors the neutral inline-SVG approach used by the trace
// waterfall / adapter sparkline — no chart library on this leaf detail view.
function TrendChart({ points }: { points: ErrorGroupTrendPoint[] }) {
  const width = 640;
  const height = 80;
  const max = Math.max(1, ...points.map((p) => p.count));
  const gap = 2;
  const barWidth = points.length > 0 ? Math.max(1, (width - gap * (points.length - 1)) / points.length) : width;

  return (
    <svg width="100%" height={height} viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" className="text-primary" role="img" aria-label="Events per hour">
      {points.map((p, i) => {
        const h = Math.max(1, (p.count / max) * (height - 2));
        const x = i * (barWidth + gap);

        return <rect key={p.hour} x={x} y={height - h} width={barWidth} height={h} rx={1} fill="currentColor" opacity={0.75} />;
      })}
    </svg>
  );
}
