import { useEffect, useMemo, useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { DataTable } from '@/components/DataTable';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { WebhookDeliveryChart } from '@/components/WebhookDeliveryChart';
import * as api from '@/api';
import { WebhookDeliveryStatus } from '@/types/webhooks';
import type { WebhookDeliveryListItem, WebhookDeliveryFilter } from '@/types/webhooks';
import { DASHBOARD_LOCALE } from '@/utils/format';

const PAGE_SIZE = 20;

// Drill-down page for one webhook group — an event type or an endpoint (destination). Shows that group's
// headline counts, its delivery-statistics chart, and its paged deliveries.
export default function WebhookGroupDetailPage() {
  const navigate = useNavigate();
  const { dim: rawDim, key: rawKey } = useParams<{ dim: string; key: string }>();
  const isEndpoint = rawDim === 'endpoint';
  // React Router already URL-decodes route params, so `rawKey` is the decoded group key (an endpoint URL
  // for the "by endpoint" dimension). A second decodeURIComponent here would corrupt keys containing '%'
  // (or throw URIError on a lone '%'), so use it as-is.
  const key = rawKey ?? '';
  const [page, setPage] = useState(0);

  // Navigating between groups reuses this component — reset to the first page so we never land out of range.
  useEffect(() => {
    setPage(0);
  }, [key, isEndpoint]);

  const scope = useMemo(() => (isEndpoint ? { group: key } : { eventType: key }), [isEndpoint, key]);

  const filter = useMemo<WebhookDeliveryFilter>(
    () => ({ ...scope, page, pageSize: PAGE_SIZE }),
    [scope, page],
  );

  const groupsQuery = useQuery({
    queryKey: ['webhooks', 'groups', isEndpoint ? 'endpoint' : 'type'] as const,
    queryFn: () => api.getWebhookGroups(isEndpoint ? 'endpoint' : 'type'),
    enabled: !!key,
  });

  const historyQuery = useQuery({
    queryKey: ['webhooks', 'history', scope] as const,
    queryFn: () => api.getWebhookDeliveryHistory(scope),
    enabled: !!key,
  });

  const listQuery = useQuery({
    queryKey: ['webhooks', 'group-list', filter] as const,
    queryFn: () => api.getWebhooks(filter),
    enabled: !!key,
  });

  const columns = useMemo<ColumnDef<WebhookDeliveryListItem>[]>(
    () => [
      {
        accessorKey: 'createdAt',
        header: 'Created',
        meta: { headerClassName: 'w-40' },
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground"><RelativeTime date={row.original.createdAt} /></span>
        ),
      },
      // On an event-type page the endpoint varies (show it); on an endpoint page the event varies (show it).
      isEndpoint
        ? { accessorKey: 'eventType', header: 'Event', cell: ({ row }) => <span className="font-medium">{row.original.eventType}</span> }
        : {
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
          row.original.reference ? <span className="font-mono text-xs">{row.original.reference}</span> : <span className="text-muted-foreground/40">—</span>,
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
    ],
    [isEndpoint],
  );

  if (listQuery.isError) return <ErrorState message="Unable to load this webhook group" />;
  if (listQuery.isLoading || !listQuery.data) return <LoadingState />;

  const paged = listQuery.data;
  const group = groupsQuery.data?.find((g) => g.key === key);
  const total = group?.total ?? paged.totalCount;

  return (
    <div>
      <div className="mb-4">
        <Link to="/webhooks" className="text-sm text-muted-foreground hover:underline">← Webhooks</Link>
        <div className="flex items-center gap-3 mt-1">
          <span className="rounded bg-muted px-1.5 py-0.5 text-xs font-semibold uppercase text-muted-foreground">
            {isEndpoint ? 'Endpoint' : 'Event type'}
          </span>
          <h1 className="text-2xl font-bold font-mono break-all">{key}</h1>
        </div>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
        <Tile label="Deliveries" value={total.toLocaleString(DASHBOARD_LOCALE)} />
        <Tile label="Delivered" value={(group?.delivered ?? 0).toLocaleString(DASHBOARD_LOCALE)} />
        <Tile label="Pending" value={(group?.pending ?? 0).toLocaleString(DASHBOARD_LOCALE)} />
        <Tile
          label="Exhausted"
          value={(group?.exhausted ?? 0).toLocaleString(DASHBOARD_LOCALE)}
          emphasis={group && group.exhausted > 0 ? 'text-destructive' : undefined}
        />
      </div>

      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Delivery statistics</CardTitle></CardHeader>
        <CardContent>
          <WebhookDeliveryChart points={historyQuery.data ?? []} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle className="text-base">Deliveries</CardTitle></CardHeader>
        <CardContent className="p-0">
          <DataTable
            columns={columns}
            data={paged.items}
            emptyMessage="No deliveries in this group."
            getRowId={(row) => row.id}
            onRowClick={(row) => navigate(`/webhooks/${encodeURIComponent(row.id)}`)}
            pagination={{ page, pageSize: PAGE_SIZE, pageCount: paged.pageCount, onPageChange: setPage }}
          />
        </CardContent>
      </Card>
    </div>
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

const statusStyles: Record<WebhookDeliveryStatus, { label: string; cls: string }> = {
  [WebhookDeliveryStatus.Pending]: { label: 'Pending', cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400' },
  [WebhookDeliveryStatus.Delivered]: { label: 'Delivered', cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' },
  [WebhookDeliveryStatus.Exhausted]: { label: 'Exhausted', cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' },
};

function StatusPill({ status }: { status: WebhookDeliveryStatus }) {
  const style = statusStyles[status] ?? { label: 'Unknown', cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400' };

  return <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${style.cls}`}>{style.label}</span>;
}
