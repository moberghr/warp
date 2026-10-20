import { useState, useEffect, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { Badge } from '@/components/ui/badge';
import { shortId, shortType } from '@/utils/format';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import type { WorkerDetailModel, WorkerJobLogModel, PagedSlice } from '@/types';
import * as api from '@/api';

const eventColors: Record<string, string> = {
  Processing: 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200',
  Completed: 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200',
  Failed: 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200',
  Cancelled: 'bg-orange-100 text-orange-800 dark:bg-orange-900 dark:text-orange-200',
  Requeued: 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200',
  Log: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200',
};

const levelColors: Record<string, string> = {
  Information: 'text-blue-700 dark:text-blue-400',
  Warning: 'text-yellow-700 dark:text-yellow-400',
  Error: 'text-red-700 dark:text-red-400',
};

export default function WorkerDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [worker, setWorker] = useState<WorkerDetailModel | null>(null);
  const [data, setData] = useState<PagedSlice<WorkerJobLogModel> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  useEffect(() => {
    if (!id) return;
    api.getWorkerById(id).then(setWorker).catch(() => {});
  }, [id]);

  const fetchData = useCallback(async () => {
    if (!id) return;
    try {
      const result = await api.getWorkerJobLogs(id, page, pageSize);
      setData(result);
      setError(null);
    } catch {
      setError('Unable to load worker logs');
    }
  }, [id, page, pageSize]);

  useEffect(() => { fetchData(); }, [fetchData]);

  if (error) return <ErrorState message={error} />;
  if (!data) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center gap-3 mb-6">
        <span className={`inline-block w-3 h-3 rounded-full ${worker?.currentJobId ? 'bg-purple-500' : 'bg-green-500'}`} />
        <h1 className="text-2xl font-bold">Worker {shortId(id!)}</h1>
        {worker?.currentJobId ? (
          <span className="text-sm text-purple-700 dark:text-purple-400 font-medium">Processing</span>
        ) : (
          <span className="text-sm text-muted-foreground">Idle</span>
        )}
        {worker?.serverPausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Server Paused</Badge>}
        {worker?.workerGroupPausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Group Paused</Badge>}
      </div>

      {worker && (
        <Card className="mb-6">
          <CardHeader className="pb-2"><CardTitle className="text-sm">Details</CardTitle></CardHeader>
          <CardContent className="grid grid-cols-1 md:grid-cols-2 gap-x-8 gap-y-2 text-sm">
            <div>
              <span className="text-muted-foreground">Server:</span>{' '}
              <Link to={`/servers/${worker.serverId}`} className="text-primary hover:underline">{worker.serverName}</Link>
            </div>
            <div>
              <span className="text-muted-foreground">Started:</span>{' '}
              <RelativeTime date={worker.startedTime} />
            </div>
            <div>
              <span className="text-muted-foreground">Heartbeat:</span>{' '}
              {worker.lastHeartbeatTime ? <RelativeTime date={worker.lastHeartbeatTime} /> : 'N/A'}
            </div>
            <div>
              <span className="text-muted-foreground">Current Job:</span>{' '}
              {worker.currentJobId ? (
                <>
                  <Link to={`/detail/${worker.currentJobId}`} className="text-primary hover:underline font-mono text-xs">
                    {shortId(worker.currentJobId)}
                  </Link>
                  {worker.currentJobType && (
                    <span className="text-muted-foreground ml-2">({shortType(worker.currentJobType)})</span>
                  )}
                </>
              ) : (
                <span className="text-muted-foreground">None</span>
              )}
            </div>
            <div className="md:col-span-2">
              <span className="text-muted-foreground">ID:</span>{' '}
              <span className="font-mono text-xs">{id}</span>
            </div>
          </CardContent>
        </Card>
      )}

      <div className="flex items-center justify-between mb-4">
        <h2 className="text-lg font-semibold">Job Activity</h2>
      </div>

      <div className="rounded-md border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Event</TableHead>
              <TableHead>Job</TableHead>
              <TableHead>Type</TableHead>
              <TableHead>Message</TableHead>
              <TableHead>Duration</TableHead>
              <TableHead>Time</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.items.length === 0 ? (
              <TableRow>
                <TableCell colSpan={6} className="text-center text-muted-foreground py-8">
                  No log entries found for this worker
                </TableCell>
              </TableRow>
            ) : (
              data.items.map((log) => (
                <TableRow key={log.id}>
                  <TableCell>
                    <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${eventColors[log.eventType] ?? 'text-muted-foreground'}`}>
                      {log.eventType}
                    </span>
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    <Link to={`/detail/${log.jobId}`} className="text-primary hover:underline">
                      {shortId(log.jobId)}
                    </Link>
                  </TableCell>
                  <TableCell className="text-sm">{log.jobType ? shortType(log.jobType) : '-'}</TableCell>
                  <TableCell className={`text-sm max-w-[300px] truncate ${levelColors[log.level] ?? 'text-muted-foreground'}`}>
                    {log.message}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {log.durationMs != null ? `${log.durationMs.toFixed(0)}ms` : '-'}
                  </TableCell>
                  <TableCell className="text-sm">
                    <RelativeTime date={log.timestamp} />
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </div>

      {/* No total: the server pages previous/next so it never counts the worker's whole history. */}
      <Pagination page={page} pageCount={0} hasNext={data.hasMore} onPageChange={setPage} pageSize={pageSize} onPageSizeChange={(size) => { setPageSize(size); setPage(0); }} />
    </div>
  );
}
