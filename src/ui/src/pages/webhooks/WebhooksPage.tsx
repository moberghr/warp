import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { X } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { DataTable } from '@/components/DataTable';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { WebhookDeliveryStatus } from '@/types/webhooks';
import type { WebhookDeliveryListItem, WebhookDeliveryFilter, WebhookGroupModel } from '@/types/webhooks';

const PAGE_SIZE = 20;

export default function WebhooksPage() {
  const navigate = useNavigate();
  const [statusFilter, setStatusFilter] = useState<string>('');
  const [eventFilter, setEventFilter] = useState<string>('');
  const [groupFilter, setGroupFilter] = useState<string>('');
  const [referenceFilter, setReferenceFilter] = useState<string>('');
  const [page, setPage] = useState(0);

  const filter = useMemo<WebhookDeliveryFilter>(
    () => ({
      status: statusFilter ? (Number(statusFilter) as WebhookDeliveryStatus) : undefined,
      eventType: eventFilter || undefined,
      group: groupFilter || undefined,
      reference: referenceFilter || undefined,
      page,
      pageSize: PAGE_SIZE,
    }),
    [statusFilter, eventFilter, groupFilter, referenceFilter, page],
  );

  // Any filter change resets to the first page so the view never lands past the last page.
  useEffect(() => {
    setPage(0);
  }, [statusFilter, eventFilter, groupFilter, referenceFilter]);

  const listQuery = useQuery({
    queryKey: ['webhooks', 'list', filter] as const,
    queryFn: () => api.getWebhooks(filter),
  });

  const summaryQuery = useQuery({
    queryKey: ['webhooks', 'summary'] as const,
    queryFn: () => api.getWebhookSummary(),
  });

  const typeGroupsQuery = useQuery({
    queryKey: ['webhooks', 'groups', 'type'] as const,
    queryFn: () => api.getWebhookGroups('type'),
  });

  const endpointGroupsQuery = useQuery({
    queryKey: ['webhooks', 'groups', 'endpoint'] as const,
    queryFn: () => api.getWebhookGroups('endpoint'),
  });

  const columns = useMemo<ColumnDef<WebhookDeliveryListItem>[]>(
    () => [
      {
        accessorKey: 'createdAt',
        header: 'Created',
        meta: { headerClassName: 'w-40' },
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.createdAt} />
          </span>
        ),
      },
      {
        accessorKey: 'eventType',
        header: 'Event',
        cell: ({ row }) => <span className="font-medium">{row.original.eventType}</span>,
      },
      {
        id: 'endpoint',
        header: 'Endpoint',
        cell: ({ row }) => (
          <span className="font-mono text-xs text-muted-foreground truncate block max-w-xs">
            {row.original.groupName ?? row.original.url}
          </span>
        ),
      },
      {
        accessorKey: 'reference',
        header: 'Reference',
        meta: { headerClassName: 'w-40' },
        cell: ({ row }) =>
          row.original.reference ? (
            <span className="font-mono text-xs">{row.original.reference}</span>
          ) : (
            <span className="text-muted-foreground/40">—</span>
          ),
      },
      {
        accessorKey: 'status',
        header: 'Status',
        meta: { headerClassName: 'w-28' },
        cell: ({ row }) => <StatusPill status={row.original.status} />,
      },
      {
        accessorKey: 'attemptCount',
        header: 'Attempts',
        meta: { headerClassName: 'text-right w-24', cellClassName: 'text-right tabular-nums' },
        cell: ({ row }) => row.original.attemptCount,
      },
      {
        accessorKey: 'nextAttemptAt',
        header: 'Next attempt',
        meta: { headerClassName: 'w-40' },
        cell: ({ row }) =>
          row.original.nextAttemptAt ? (
            <span className="text-sm text-muted-foreground">
              <RelativeTime date={row.original.nextAttemptAt} />
            </span>
          ) : (
            <span className="text-muted-foreground/40">—</span>
          ),
      },
    ],
    [],
  );

  if (listQuery.isError) return <ErrorState message="Unable to load webhook deliveries" />;
  if (listQuery.isLoading || !listQuery.data) return <LoadingState />;

  const paged = listQuery.data;
  const summary = summaryQuery.data;
  // The summary is a separate query from the deliveries list: when it fails we must not paint
  // fake-healthy zero tiles (0 deliveries / 0% delivered reads as "all clear"). Show em-dashes and an
  // inline note instead, while the deliveries list below stays fully usable.
  const summaryError = summaryQuery.isError;
  const deliveredPercent =
    summary && summary.total > 0 ? Math.round((summary.delivered / summary.total) * 100) : 0;

  return (
    <div>
      <h1 className="text-2xl font-bold mb-1">Webhooks</h1>
      <p className="text-sm text-muted-foreground mb-4">
        Durable outbound delivery — the delivery is the state machine; attempts are recorded as adapter calls.
      </p>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
        <Tile label="Deliveries" value={summaryError ? '—' : (summary?.total ?? 0).toLocaleString()} />
        <Tile label="Delivered" value={summaryError ? '—' : `${deliveredPercent}%`} />
        <Tile label="Pending" value={summaryError ? '—' : (summary?.pending ?? 0).toLocaleString()} />
        <Tile
          label="Exhausted"
          value={summaryError ? '—' : (summary?.exhausted ?? 0).toLocaleString()}
          emphasis={!summaryError && summary && summary.exhausted > 0 ? 'text-destructive' : undefined}
        />
      </div>

      {summaryError && (
        <p className="text-sm text-destructive mb-4">Delivery totals are unavailable right now.</p>
      )}

      {/* Group webhooks by type + by endpoint — click a row to filter the deliveries list below. */}
      <div className="grid gap-4 md:grid-cols-2 mb-4">
        <GroupCard
          title="By event type"
          groups={typeGroupsQuery.data}
          keyHeader="Event type"
          activeKey={eventFilter}
          onSelect={(key) => setEventFilter((prev) => (prev === key ? '' : key))}
        />
        <GroupCard
          title="By endpoint"
          groups={endpointGroupsQuery.data}
          keyHeader="Endpoint"
          activeKey={groupFilter}
          onSelect={(key) => setGroupFilter((prev) => (prev === key ? '' : key))}
        />
      </div>

      <div className="flex flex-wrap items-center gap-2 mb-4">
        <select
          className="border rounded-md px-2 py-1 text-sm bg-background"
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
        >
          <option value="">All statuses</option>
          <option value={WebhookDeliveryStatus.Pending}>Pending</option>
          <option value={WebhookDeliveryStatus.Delivered}>Delivered</option>
          <option value={WebhookDeliveryStatus.Exhausted}>Exhausted</option>
        </select>
        <input
          type="text"
          className="border rounded-md px-2 py-1 text-sm bg-background flex-1 max-w-xs"
          placeholder="Filter by reference…"
          value={referenceFilter}
          onChange={(e) => setReferenceFilter(e.target.value)}
        />
        {eventFilter && <FilterChip label={`Type: ${eventFilter}`} onClear={() => setEventFilter('')} />}
        {groupFilter && <FilterChip label={`Endpoint: ${groupFilter}`} onClear={() => setGroupFilter('')} />}
      </div>

      <DataTable
        columns={columns}
        data={paged.items}
        emptyMessage="No webhook deliveries match the current filters."
        getRowId={(row) => row.id}
        onRowClick={(row) => navigate(`/webhooks/${encodeURIComponent(row.id)}`)}
        pagination={{
          page,
          pageSize: PAGE_SIZE,
          pageCount: paged.pageCount,
          onPageChange: setPage,
        }}
      />
    </div>
  );
}

// A grouped-summary table (by type or by endpoint). Each row is clickable to filter the deliveries list;
// the active row is highlighted. Exhausted counts render in the destructive colour so a bad group stands out.
function GroupCard({
  title,
  groups,
  keyHeader,
  activeKey,
  onSelect,
}: {
  title: string;
  groups: WebhookGroupModel[] | undefined;
  keyHeader: string;
  activeKey: string;
  onSelect: (key: string) => void;
}) {
  return (
    <Card>
      <CardHeader><CardTitle className="text-base">{title}</CardTitle></CardHeader>
      <CardContent className="p-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{keyHeader}</TableHead>
              <TableHead className="text-right w-20">Total</TableHead>
              <TableHead className="text-right w-24">Delivered</TableHead>
              <TableHead className="text-right w-20">Pending</TableHead>
              <TableHead className="text-right w-24">Exhausted</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {!groups || groups.length === 0 ? (
              <TableRow>
                <TableCell colSpan={5} className="text-center text-muted-foreground py-6">
                  No deliveries yet.
                </TableCell>
              </TableRow>
            ) : (
              groups.map((g) => (
                <TableRow
                  key={g.key}
                  className={`cursor-pointer ${activeKey === g.key ? 'bg-accent' : ''}`}
                  onClick={() => onSelect(g.key)}
                  title="Filter deliveries by this group"
                >
                  <TableCell className="font-mono text-xs truncate max-w-[16rem]">{g.key}</TableCell>
                  <TableCell className="text-right tabular-nums">{g.total.toLocaleString()}</TableCell>
                  <TableCell className="text-right tabular-nums text-green-600 dark:text-green-400">{g.delivered.toLocaleString()}</TableCell>
                  <TableCell className="text-right tabular-nums text-amber-600 dark:text-amber-400">{g.pending.toLocaleString()}</TableCell>
                  <TableCell className={`text-right tabular-nums ${g.exhausted > 0 ? 'text-destructive' : ''}`}>{g.exhausted.toLocaleString()}</TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </CardContent>
    </Card>
  );
}

function FilterChip({ label, onClear }: { label: string; onClear: () => void }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-primary/10 text-primary px-2 py-0.5 text-xs font-medium">
      {label}
      <button type="button" onClick={onClear} className="rounded-full hover:bg-primary/20 p-0.5" title="Clear filter">
        <X className="h-3 w-3" />
      </button>
    </span>
  );
}

function Tile({ label, value, emphasis }: { label: string; value: string; emphasis?: string }) {
  return (
    <Card>
      <CardContent className="p-4">
        <div className="text-sm text-muted-foreground">{label}</div>
        <div className={`text-2xl font-bold ${emphasis ?? ''}`}>{value}</div>
      </CardContent>
    </Card>
  );
}

// Delivery-status pill. Pending is in-flight (amber), Delivered settled OK (green), Exhausted
// settled after the schedule ran out (red). Duplicated (small) in the detail page rather than
// factored into a shared module to keep the deliveries list its own lazy chunk.
const statusStyles: Record<WebhookDeliveryStatus, { label: string; cls: string }> = {
  [WebhookDeliveryStatus.Pending]: {
    label: 'Pending',
    cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
  },
  [WebhookDeliveryStatus.Delivered]: {
    label: 'Delivered',
    cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400',
  },
  [WebhookDeliveryStatus.Exhausted]: {
    label: 'Exhausted',
    cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400',
  },
};

function StatusPill({ status }: { status: WebhookDeliveryStatus }) {
  const style = statusStyles[status] ?? {
    label: 'Unknown',
    cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400',
  };

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${style.cls}`}>
      {style.label}
    </span>
  );
}
