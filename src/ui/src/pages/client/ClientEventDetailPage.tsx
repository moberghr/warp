import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import { ClientEventType } from '@/types/client';

// Detail for one client (browser) event: full message/stack/properties/breadcrumbs plus the session and (for
// a request) the trace it belongs to — the drill-down from the Client event stream.
export default function ClientEventDetailPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const id = useMemo(() => {
    if (!rawId) return '';
    try { return decodeURIComponent(rawId); } catch { return rawId; }
  }, [rawId]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['client', 'event', id] as const,
    queryFn: () => api.getClientEvent(id),
  });

  if (isError) return <ErrorState message="Event not found or it has been cleaned up" />;
  if (isLoading || !data) return <LoadingState />;

  const typeLabel = TYPE_LABELS[data.type] ?? 'client';

  return (
    <div>
      <div className="mb-4">
        <Link to="/client" className="text-sm text-primary hover:underline">← Client</Link>
        <h1 className="text-2xl font-bold mt-1">
          <span className="uppercase text-muted-foreground text-lg mr-2">{typeLabel}</span>
          <span className="font-mono">{data.name ?? data.message ?? '(event)'}</span>
        </h1>
      </div>

      <Card className="mb-4">
        <CardContent className="p-4 grid grid-cols-1 md:grid-cols-2 gap-x-8 gap-y-2 text-sm">
          <Field label="Application" value={data.application} />
          <Field label="Level" value={data.level} />
          <Field label="Value" value={data.value != null ? String(data.value) : null} />
          <Field label="Page" value={data.url} />
          <Field label="Release" value={data.release} />
          <Field label="User agent" value={data.userAgent} />
          <Field label="Caller IP" value={data.remoteIp} />
          <Field label="Timestamp" value={data.timestamp} relative />
          <Field label="Received" value={data.receivedAt} relative />
          <div className="flex gap-2">
            <span className="text-muted-foreground w-28 shrink-0">Session</span>
            {data.sessionId ? (
              <Link to={`/client/sessions/${encodeURIComponent(data.sessionId)}`} className="font-mono text-primary hover:underline">{data.sessionId}</Link>
            ) : <span>—</span>}
          </div>
          <div className="flex gap-2">
            <span className="text-muted-foreground w-28 shrink-0">Trace</span>
            {data.traceId ? (
              <Link to={`/trace/${data.traceId}`} className="font-mono text-primary hover:underline">{data.traceId}</Link>
            ) : <span>—</span>}
          </div>
        </CardContent>
      </Card>

      {data.message && <Block title="Message" body={data.message} />}
      {data.stack && <Block title="Stack" body={data.stack} mono />}
      {data.properties && <Block title="Properties" body={pretty(data.properties)} mono />}
      {data.breadcrumbs && <Block title="Breadcrumbs" body={pretty(data.breadcrumbs)} mono />}
    </div>
  );
}

const TYPE_LABELS: Record<number, string> = {
  [ClientEventType.Error]: 'error',
  [ClientEventType.Vital]: 'vital',
  [ClientEventType.Log]: 'log',
  [ClientEventType.Event]: 'event',
  [ClientEventType.Request]: 'request',
};

function Field({ label, value, relative }: { label: string; value: string | null | undefined; relative?: boolean }) {
  return (
    <div className="flex gap-2">
      <span className="text-muted-foreground w-28 shrink-0">{label}</span>
      {value ? (relative ? <RelativeTime date={value} /> : <span className="break-all">{value}</span>) : <span>—</span>}
    </div>
  );
}

function Block({ title, body, mono }: { title: string; body: string; mono?: boolean }) {
  return (
    <div className="mb-4">
      <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-2">{title}</h2>
      <Card>
        <CardContent className="p-3">
          <pre className={`whitespace-pre-wrap break-all text-sm ${mono ? 'font-mono' : ''}`}>{body}</pre>
        </CardContent>
      </Card>
    </div>
  );
}

function pretty(raw: string): string {
  try { return JSON.stringify(JSON.parse(raw), null, 2); } catch { return raw; }
}
