import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { Card, CardContent } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import { ClientEventType } from '@/types/client';
import type { ClientEventItem, ClientVitalStat } from '@/types/client';

// Client (browser) observability (§8.27): errors, logs, web vitals and custom events reported by a frontend
// app through the Warp ingest endpoint. Summary tiles + web-vital p75s (Google-colored) come from the durable
// fold; the event stream reads recent raw rows.
export default function ClientPage() {
  const [type, setType] = useState<ClientEventType | undefined>(undefined);

  const summary = useQuery({
    queryKey: ['client', 'summary'] as const,
    queryFn: () => api.getClientSummary(),
    refetchInterval: 30_000,
  });

  const events = useQuery({
    queryKey: ['client', 'events', type] as const,
    queryFn: () => api.getClientEvents({ type, pageSize: 50 }),
    refetchInterval: 30_000,
  });

  const columns = useMemo<ColumnDef<ClientEventItem>[]>(
    () => [
      {
        accessorKey: 'type',
        header: 'Type',
        cell: ({ row }) => <TypeBadge type={row.original.type} />,
        meta: { headerClassName: 'w-[90px]' },
      },
      {
        accessorKey: 'name',
        header: 'Name / message',
        cell: ({ row }) => (
          <div className="min-w-0">
            {row.original.name && <span className="font-mono text-xs">{row.original.name}</span>}
            {row.original.message && <span className="block truncate text-sm text-muted-foreground">{row.original.message}</span>}
            {row.original.value != null && <span className="text-sm tabular-nums">{row.original.value}</span>}
          </div>
        ),
      },
      {
        accessorKey: 'url',
        header: 'Page',
        cell: ({ row }) => <span className="text-xs text-muted-foreground">{row.original.url ?? '—'}</span>,
        meta: { headerClassName: 'w-[180px]' },
      },
      {
        accessorKey: 'sessionId',
        header: 'Session',
        cell: ({ row }) =>
          row.original.sessionId ? (
            <Link to={`/client/sessions/${encodeURIComponent(row.original.sessionId)}`} className="font-mono text-xs text-primary hover:underline">
              {row.original.sessionId.slice(0, 8)}
            </Link>
          ) : (
            <span className="text-muted-foreground">—</span>
          ),
        meta: { headerClassName: 'w-[100px]' },
      },
      {
        accessorKey: 'timestamp',
        header: 'When',
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.timestamp} />
          </span>
        ),
        meta: { headerClassName: 'w-[120px] text-right', cellClassName: 'text-right' },
      },
    ],
    [],
  );

  if (summary.isError) return <ErrorState message="Unable to load client observability" />;
  if (summary.isLoading || !summary.data) return <LoadingState />;

  const s = summary.data;

  return (
    <div>
      <div className="mb-4">
        <h1 className="text-2xl font-bold">Client</h1>
        <p className="text-sm text-muted-foreground mt-1">Errors, logs, web vitals and custom events reported by your frontend apps.</p>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-5 gap-3 mb-4">
        <MetricTile label="Error rate" value={`${(Math.round(s.errorRate * 1000) / 10).toFixed(1)}%`} emphasis={s.errorRate > 0 ? 'text-destructive' : undefined} />
        <MetricTile label="Errors" value={s.errorCount.toLocaleString()} />
        <MetricTile label="Logs" value={s.logCount.toLocaleString()} />
        <MetricTile label="Events" value={s.eventCount.toLocaleString()} />
        <MetricTile label="Vitals" value={s.vitalCount.toLocaleString()} />
      </div>

      {s.vitals.length > 0 && (
        <div className="mb-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-2">Core Web Vitals (p75)</h2>
          <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
            {s.vitals.map((v) => (
              <VitalTile key={v.name} vital={v} />
            ))}
          </div>
        </div>
      )}

      {s.topErrors.length > 0 && (
        <div className="mb-4">
          <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-2">Top errors</h2>
          <Card>
            <CardContent className="p-3 space-y-1">
              {s.topErrors.map((e) => (
                <div key={e.name} className="flex justify-between text-sm">
                  <span className="font-mono truncate">{e.name}</span>
                  <span className="tabular-nums text-muted-foreground">{e.count.toLocaleString()}</span>
                </div>
              ))}
            </CardContent>
          </Card>
        </div>
      )}

      <div className="flex items-center justify-between mb-2">
        <h2 className="text-sm font-semibold text-muted-foreground uppercase">Recent events</h2>
        <div className="inline-flex rounded-md border p-0.5 text-xs">
          <FilterButton active={type === undefined} onClick={() => setType(undefined)}>All</FilterButton>
          <FilterButton active={type === ClientEventType.Error} onClick={() => setType(ClientEventType.Error)}>Errors</FilterButton>
          <FilterButton active={type === ClientEventType.Log} onClick={() => setType(ClientEventType.Log)}>Logs</FilterButton>
          <FilterButton active={type === ClientEventType.Event} onClick={() => setType(ClientEventType.Event)}>Events</FilterButton>
          <FilterButton active={type === ClientEventType.Request} onClick={() => setType(ClientEventType.Request)}>Requests</FilterButton>
          <FilterButton active={type === ClientEventType.Vital} onClick={() => setType(ClientEventType.Vital)}>Vitals</FilterButton>
        </div>
      </div>

      {events.isLoading || !events.data ? (
        <LoadingState />
      ) : (
        <DataTable columns={columns} data={events.data.items} emptyMessage="No client events recorded yet" getRowId={(row) => row.id} />
      )}
    </div>
  );
}

// Google Core Web Vitals p75 thresholds (ms; CLS unitless): good / needs-improvement / poor.
const THRESHOLDS: Record<string, { good: number; poor: number }> = {
  LCP: { good: 2500, poor: 4000 },
  INP: { good: 200, poor: 500 },
  CLS: { good: 0.1, poor: 0.25 },
  FCP: { good: 1800, poor: 3000 },
  TTFB: { good: 800, poor: 1800 },
};

function VitalTile({ vital }: { vital: ClientVitalStat }) {
  const t = THRESHOLDS[vital.name];
  const rating = !t ? 'muted' : vital.p75Value <= t.good ? 'good' : vital.p75Value <= t.poor ? 'ni' : 'poor';
  const color = rating === 'good' ? 'text-green-600 dark:text-green-400' : rating === 'ni' ? 'text-amber-600 dark:text-amber-400' : rating === 'poor' ? 'text-destructive' : '';
  const display = vital.name === 'CLS' ? vital.p75Value.toFixed(3) : `${Math.round(vital.p75Value)}ms`;

  return (
    <Card>
      <CardContent className="p-3">
        <div className="text-xs text-muted-foreground">{vital.name}</div>
        <div className={`text-xl font-bold tabular-nums ${color}`}>{display}</div>
        <div className="text-xs text-muted-foreground">{vital.sampleCount.toLocaleString()} samples</div>
      </CardContent>
    </Card>
  );
}

function MetricTile({ label, value, emphasis }: { label: string; value: string; emphasis?: string }) {
  return (
    <Card>
      <CardContent className="p-3">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={`text-xl font-bold tabular-nums ${emphasis ?? ''}`}>{value}</div>
      </CardContent>
    </Card>
  );
}

function FilterButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`px-2.5 py-1 rounded font-medium transition-colors ${active ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground'}`}
    >
      {children}
    </button>
  );
}

function TypeBadge({ type }: { type: ClientEventType }) {
  const map: Record<number, { label: string; cls: string }> = {
    [ClientEventType.Error]: { label: 'Error', cls: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
    [ClientEventType.Vital]: { label: 'Vital', cls: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
    [ClientEventType.Log]: { label: 'Log', cls: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300' },
    [ClientEventType.Event]: { label: 'Event', cls: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
    [ClientEventType.Request]: { label: 'Request', cls: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
  };
  const b = map[type] ?? { label: 'Unknown', cls: 'bg-gray-100 text-gray-700' };

  return <span className={`inline-block rounded px-1.5 py-0.5 text-xs font-medium ${b.cls}`}>{b.label}</span>;
}
