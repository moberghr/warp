import { useState, useEffect } from 'react';
import { useParams, Link } from 'react-router-dom';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { Badge } from '@/components/ui/badge';
import { usePageStore } from '@/stores/page';
import { shortId, shortType } from '@/utils/format';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { useWorkerDetail, useWorkerLogs } from '@/api/hooks/useServers';

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
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  const workerQuery = useWorkerDetail(id);
  const logsQuery = useWorkerLogs(id, page, pageSize);

  const worker = workerQuery.data ?? null;

  useEffect(() => {
    usePageStore.getState().set({
      title: `Worker ${shortId(id ?? '')}`,
      subtitle: worker?.currentJobId ? 'Processing' : 'Idle',
      right: (
        <div className="flex items-center gap-2">
          {worker?.serverPausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Server Paused</Badge>}
          {worker?.workerGroupPausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Group Paused</Badge>}
        </div>
      ),
    });
  }, [id, worker]);

  useEffect(() => {
    return () => usePageStore.getState().reset();
  }, []);

  if (logsQuery.error) return <ErrorState message={(logsQuery.error as Error).message} />;
  if (!logsQuery.data) return <LoadingState />;

  const data = logsQuery.data;

  return (
    <div className="flex flex-col gap-3 py-5">
      <div className="flex items-center gap-2 text-[12.5px] text-text-mute">
        <span className={`inline-block w-3 h-3 rounded-full ${worker?.currentJobId ? 'bg-purple-500' : 'bg-green-500'}`} />
        {worker?.currentJobId ? 'Processing' : 'Idle'}
      </div>

      {worker && (
        <Panel>
          <PanelHeader eyebrow="Details" />
          <div className="px-4 py-3">
            <dl className="grid grid-cols-[140px_1fr] gap-x-4 gap-y-2 text-[13px]">
              <dt className="warp-eyebrow text-text-mute">Server</dt>
              <dd>
                <Link to={`/servers/${worker.serverId}`} className="text-primary hover:underline">{worker.serverName}</Link>
              </dd>
              <dt className="warp-eyebrow text-text-mute">Started</dt>
              <dd><RelativeTime date={worker.startedTime} /></dd>
              <dt className="warp-eyebrow text-text-mute">Heartbeat</dt>
              <dd>{worker.lastHeartbeatTime ? <RelativeTime date={worker.lastHeartbeatTime} /> : 'N/A'}</dd>
              <dt className="warp-eyebrow text-text-mute">Current job</dt>
              <dd>
                {worker.currentJobId ? (
                  <>
                    <Link to={`/detail/${worker.currentJobId}`} className="text-primary hover:underline font-mono text-xs">
                      {shortId(worker.currentJobId)}
                    </Link>
                    {worker.currentJobType && (
                      <span className="text-text-mute ml-2">({shortType(worker.currentJobType)})</span>
                    )}
                  </>
                ) : (
                  <span className="text-text-mute">None</span>
                )}
              </dd>
              <dt className="warp-eyebrow text-text-mute">ID</dt>
              <dd className="font-mono text-xs">{id}</dd>
            </dl>
          </div>
        </Panel>
      )}

      <Panel className="overflow-hidden">
        <PanelHeader eyebrow="Job activity" action={<span className="text-[11px] text-text-mute">{data.totalCount} log entries</span>} />
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Event</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Job</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Type</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Message</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Duration</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Time</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={6} className="text-center text-text-mute py-8 text-[13px]">
                    No log entries found for this worker
                  </td>
                </tr>
              ) : (
                data.items.map((log) => (
                  <tr key={log.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                    <td className="px-3.5 py-2 text-[12.5px]">
                      <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${eventColors[log.eventType] ?? 'text-text-mute'}`}>
                        {log.eventType}
                      </span>
                    </td>
                    <td className="px-3.5 py-2 font-mono text-[12.5px]">
                      <Link to={`/detail/${log.jobId}`} className="text-primary hover:underline">
                        {shortId(log.jobId)}
                      </Link>
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">{log.jobType ? shortType(log.jobType) : '-'}</td>
                    <td className={`px-3.5 py-2 text-[12.5px] max-w-[300px] truncate ${levelColors[log.level] ?? 'text-text-mute'}`}>
                      {log.message}
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px] text-text-mute">
                      {log.durationMs != null ? `${log.durationMs.toFixed(0)}ms` : '-'}
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">
                      <RelativeTime date={log.timestamp} />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <Pagination page={page} pageCount={data.pageCount} onPageChange={setPage} pageSize={pageSize} onPageSizeChange={(size) => { setPageSize(size); setPage(0); }} />
    </div>
  );
}
