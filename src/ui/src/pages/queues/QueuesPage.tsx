import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { Card, CardContent } from '@/components/ui/card';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import * as api from '@/api';
import type { QueueMetricModel } from '@/types/applications';

// Per-queue SLIs (§8.26): queue-wait latency (avg + p95/p99, from the durable qwait: fold — survives Job-row
// cleanup) alongside the latest backlog gauge (depth + oldest-age, sampled by the BacklogSampler server task).
export default function QueuesPage() {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['queues', 'metrics'] as const,
    queryFn: () => api.getQueueMetrics(),
    refetchInterval: 30_000,
  });

  const columns = useMemo<ColumnDef<QueueMetricModel>[]>(
    () => [
      {
        accessorKey: 'queue',
        header: 'Queue',
        cell: ({ row }) => <span className="font-mono text-sm">{row.original.queue}</span>,
      },
      {
        accessorKey: 'backlogDepth',
        header: 'Backlog',
        cell: ({ row }) => (
          <span className={`tabular-nums ${row.original.backlogDepth > 0 ? 'font-semibold' : 'text-muted-foreground'}`}>
            {row.original.backlogDepth.toLocaleString()}
          </span>
        ),
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'oldestAgeSeconds',
        header: 'Oldest',
        cell: ({ row }) => (
          <span className="tabular-nums text-muted-foreground">{formatAge(row.original.oldestAgeSeconds)}</span>
        ),
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'claimedCount',
        header: 'Claimed',
        cell: ({ row }) => <span className="tabular-nums text-muted-foreground">{row.original.claimedCount.toLocaleString()}</span>,
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'avgWaitMs',
        header: 'Avg wait',
        cell: ({ row }) => <span className="tabular-nums">{formatDuration(row.original.avgWaitMs)}</span>,
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'p95WaitMs',
        header: 'p95 wait',
        cell: ({ row }) => <span className="tabular-nums">{formatDuration(row.original.p95WaitMs)}</span>,
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
      {
        accessorKey: 'p99WaitMs',
        header: 'p99 wait',
        cell: ({ row }) => <span className="tabular-nums">{formatDuration(row.original.p99WaitMs)}</span>,
        meta: { headerClassName: 'w-[110px] text-right', cellClassName: 'text-right' },
      },
    ],
    [],
  );

  if (isError) return <ErrorState message="Unable to load queue metrics" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <div className="mb-4">
        <h1 className="text-2xl font-bold">Queues</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Time jobs spend waiting to be claimed, and how deep each queue is backed up.
        </p>
      </div>

      {data.queues.length === 0 ? (
        <Card>
          <CardContent className="py-8 text-center text-sm text-muted-foreground">
            No queue metrics recorded yet. Metrics appear once jobs are claimed and the backlog sampler runs.
          </CardContent>
        </Card>
      ) : (
        <DataTable columns={columns} data={data.queues} emptyMessage="No queues" getRowId={(row) => row.queue} />
      )}
    </div>
  );
}

function formatDuration(ms: number): string {
  if (ms <= 0) {
    return '—';
  }
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }

  return `${(ms / 1000).toFixed(2)}s`;
}

function formatAge(seconds: number): string {
  if (seconds <= 0) {
    return '—';
  }
  if (seconds < 60) {
    return `${seconds}s`;
  }
  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  }

  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}
