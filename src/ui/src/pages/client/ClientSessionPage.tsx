import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import { ClientEventType } from '@/types/client';
import { HttpStatus } from '@/pages/adapters/shared';
import type { ClientSessionEntry } from '@/types/client';

// The unified client<->server session timeline (§8.27): a browser session's client events (errors, logs,
// vitals, custom events, API requests) merged chronologically with the server endpoint calls they triggered
// (joined by trace id). Request/endpoint rows link out to the full job trace waterfall.
export default function ClientSessionPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const sessionId = useMemo(() => {
    if (!rawId) return '';
    try { return decodeURIComponent(rawId); } catch { return rawId; }
  }, [rawId]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['client', 'session', sessionId] as const,
    queryFn: () => api.getClientSession(sessionId),
  });

  if (isError) return <ErrorState message="Session not found or its events have been cleaned up" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="mb-4">
        <Link to="/client" className="text-sm text-primary hover:underline">← Client</Link>
        <h1 className="text-2xl font-bold mt-1">Session timeline</h1>
        <p className="text-sm text-muted-foreground mt-1">
          <span className="font-mono">{data.sessionId}</span>
          {data.application && <> · {data.application}</>}
          {' · '}client events and the server calls they triggered, in order.
        </p>
      </div>

      <Card>
        <CardContent className="p-0 divide-y">
          {data.entries.map((entry, i) => (
            <TimelineRow key={i} entry={entry} />
          ))}
          {data.entries.length === 0 && (
            <div className="py-8 text-center text-sm text-muted-foreground">No entries in this session.</div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function TimelineRow({ entry }: { entry: ClientSessionEntry }) {
  const isServer = entry.kind === 'endpoint';

  return (
    <div className={`flex items-start gap-3 px-4 py-2.5 ${isServer ? 'bg-muted/40' : ''}`}>
      <div className="w-24 shrink-0 text-xs text-muted-foreground tabular-nums pt-0.5">
        <RelativeTime date={entry.timestamp} />
      </div>
      <div className="w-16 shrink-0 pt-0.5">
        {isServer ? (
          <span className="inline-block rounded px-1.5 py-0.5 text-xs font-medium bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200">server</span>
        ) : (
          <ClientBadge type={entry.type} />
        )}
      </div>
      <div className="min-w-0 flex-1 text-sm">
        {isServer ? (
          <div className="flex items-center gap-2">
            <span className="font-mono text-xs">{entry.method} {entry.route}</span>
            {entry.statusCode != null && <HttpStatus code={entry.statusCode} className="tabular-nums text-muted-foreground" />}
            {entry.durationMs != null && <span className="tabular-nums text-muted-foreground">{Math.round(entry.durationMs)}ms</span>}
            {entry.traceId && <Link to={`/trace/${entry.traceId}`} className="text-xs text-primary hover:underline">trace →</Link>}
          </div>
        ) : (
          <div className="min-w-0">
            {entry.type === ClientEventType.Request ? (
              <div className="flex items-center gap-2">
                <span className="font-mono text-xs">{entry.name} {entry.url}</span>
                {entry.value != null && <span className="tabular-nums text-muted-foreground">{Math.round(entry.value)}ms</span>}
                {entry.traceId && <Link to={`/trace/${entry.traceId}`} className="text-xs text-primary hover:underline">trace →</Link>}
              </div>
            ) : (
              <>
                {entry.name && <span className="font-mono text-xs">{entry.name}</span>}
                {entry.level && <span className="text-xs text-muted-foreground"> [{entry.level}]</span>}
                {entry.message && <span className="block truncate text-muted-foreground">{entry.message}</span>}
                {entry.value != null && entry.type === ClientEventType.Vital && <span className="tabular-nums">{entry.value}</span>}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function ClientBadge({ type }: { type: ClientEventType | null }) {
  const map: Record<number, { label: string; cls: string }> = {
    [ClientEventType.Error]: { label: 'error', cls: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
    [ClientEventType.Vital]: { label: 'vital', cls: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
    [ClientEventType.Log]: { label: 'log', cls: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300' },
    [ClientEventType.Event]: { label: 'event', cls: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
    [ClientEventType.Request]: { label: 'request', cls: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
  };
  const b = (type != null && map[type]) || { label: 'client', cls: 'bg-gray-100 text-gray-700' };

  return <span className={`inline-block rounded px-1.5 py-0.5 text-xs font-medium ${b.cls}`}>{b.label}</span>;
}
