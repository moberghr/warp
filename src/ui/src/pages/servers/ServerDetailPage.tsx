import { useState, useEffect } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePageStore } from '@/stores/page';
import { shortId, formatBytes, serverStatusDotColor, isServerStale } from '@/utils/format';
import { ChevronDown, ChevronRight, RefreshCw, Pause, Play } from 'lucide-react';
import type { WorkerModel, ServerTaskSummary } from '@/types';
import {
  useServerDetail,
  useServerTasks,
  useServerLogs,
  usePauseServer,
  useResumeServer,
  usePauseWorkerGroup,
  useResumeWorkerGroup,
} from '@/api/hooks/useServers';
import { queryScopes } from '@/lib/queryClient';

const statusColors: Record<string, string> = {
  Completed: 'text-green-700 dark:text-green-400 bg-green-50 dark:bg-green-950/30',
  Failed: 'text-red-700 dark:text-red-400 bg-red-50 dark:bg-red-950/30',
};

export default function ServerDetailPage() {
  const { id } = useParams<{ id: string }>();
  const qc = useQueryClient();

  const serverQuery = useServerDetail(id);
  const tasksQuery = useServerTasks(id);

  const pause = usePauseServer();
  const resume = useResumeServer();

  const server = serverQuery.data;

  useEffect(() => {
    if (!server) {
      usePageStore.getState().set({ title: 'Server', subtitle: undefined });
      return;
    }

    const refetchAll = () => {
      qc.invalidateQueries({ queryKey: queryScopes.servers });
    };

    const handleTogglePause = () => {
      if (!id) return;
      if (server.pausedAt) {
        resume.mutate(id);
      } else {
        pause.mutate(id);
      }
    };

    usePageStore.getState().set({
      title: server.serverName,
      subtitle: `${server.serviceCount} worker${server.serviceCount === 1 ? '' : 's'}`,
      right: (
        <div className="flex items-center gap-2">
          {server.pausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Paused</Badge>}
          {isServerStale(server.lastHeartbeatTime) && <Badge variant="outline" className="text-red-600 border-red-300">Inactive</Badge>}
          <button onClick={refetchAll} className="p-2 rounded-md hover:bg-panel-2 text-text-mute" title="Refresh">
            <RefreshCw className="h-4 w-4" />
          </button>
          <Button
            variant="outline"
            size="sm"
            onClick={handleTogglePause}
            disabled={pause.isPending || resume.isPending}
            title={server.pausedAt ? 'Resume server (takes effect on next heartbeat, ~3s)' : 'Pause server (takes effect on next heartbeat, ~3s)'}
          >
            {server.pausedAt ? <><Play className="h-4 w-4 mr-1" /> Resume</> : <><Pause className="h-4 w-4 mr-1" /> Pause</>}
          </Button>
        </div>
      ),
    });
  }, [server, id, pause, resume, qc]);

  useEffect(() => {
    return () => usePageStore.getState().reset();
  }, []);

  if (serverQuery.error) return <ErrorState message={(serverQuery.error as Error).message} />;
  if (!server) return <LoadingState />;

  const tasks = tasksQuery.data ?? [];

  // Group by workerGroupId when available, fall back to queues|pollingMs
  const groups = new Map<string, WorkerModel[]>();
  for (const w of server.workers) {
    const key = w.workerGroupId ?? `${w.queues ?? 'default'}|${w.pollingIntervalMs ?? 1000}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key)!.push(w);
  }

  return (
    <div className="flex flex-col gap-3 p-5">
      <div className="flex items-center gap-2 text-[12.5px] text-text-mute">
        <span className={`inline-block w-3 h-3 rounded-full ${serverStatusDotColor(server.lastHeartbeatTime, server.pausedAt)}`} />
        <RelativeTime date={server.lastHeartbeatTime} />
      </div>

      <Panel>
        <PanelHeader eyebrow="Details" />
        <div className="px-4 py-3">
          <dl className="grid grid-cols-[140px_1fr] gap-x-4 gap-y-2 text-[13px]">
            <dt className="warp-eyebrow text-text-mute">Workers</dt>
            <dd className="font-mono">{server.serviceCount}</dd>
            <dt className="warp-eyebrow text-text-mute">CPU</dt>
            <dd className="font-mono">{server.cpuUsagePercent != null ? `${server.cpuUsagePercent}%` : 'N/A'}</dd>
            <dt className="warp-eyebrow text-text-mute">Memory</dt>
            <dd className="font-mono">{server.memoryWorkingSetBytes != null ? formatBytes(server.memoryWorkingSetBytes) : 'N/A'}</dd>
            <dt className="warp-eyebrow text-text-mute">Started</dt>
            <dd><RelativeTime date={server.startedTime} /></dd>
            <dt className="warp-eyebrow text-text-mute">Heartbeat</dt>
            <dd><RelativeTime date={server.lastHeartbeatTime} /></dd>
            {server.pausedAt && (
              <>
                <dt className="warp-eyebrow text-text-mute">Paused since</dt>
                <dd><RelativeTime date={server.pausedAt} /></dd>
              </>
            )}
            <dt className="warp-eyebrow text-text-mute">ID</dt>
            <dd className="font-mono text-xs">{server.id}</dd>
          </dl>
        </div>
      </Panel>

      <Panel>
        <PanelHeader eyebrow="Worker groups" />
        {groups.size > 0 ? (
          <div className="flex flex-col gap-2 p-3">
            {Array.from(groups.entries()).map(([key, workers]) => {
              const queues = workers[0].queues ?? 'default';
              const pollingMs = workers[0].pollingIntervalMs ?? 1000;
              const active = workers.filter(w => w.currentJobId).length;
              const groupId = workers[0].workerGroupId;
              const groupPausedAt = workers[0].workerGroupPausedAt;
              return (
                <WorkerGroupSection
                  key={key}
                  queues={queues}
                  pollingMs={pollingMs}
                  workers={workers}
                  activeCount={active}
                  groupId={groupId}
                  groupPausedAt={groupPausedAt}
                />
              );
            })}
          </div>
        ) : (
          <div className="py-10 text-center text-[13px] text-text-mute">No workers registered</div>
        )}
      </Panel>

      <Panel>
        <PanelHeader eyebrow="Server tasks" />
        {tasks.length === 0 ? (
          <div className="py-10 text-center text-[13px] text-text-mute">No task logs yet</div>
        ) : (
          <div className="flex flex-col gap-2 p-3">
            {tasks.map((task) => (
              <TaskSection key={task.taskName} serverId={server.id} task={task} />
            ))}
          </div>
        )}
      </Panel>
    </div>
  );
}

function WorkerGroupSection({ queues, pollingMs, workers, activeCount, groupId, groupPausedAt }: {
  queues: string;
  pollingMs: number;
  workers: WorkerModel[];
  activeCount: number;
  groupId: string | null;
  groupPausedAt: string | null;
}) {
  const [expanded, setExpanded] = useState(false);
  const pauseGroup = usePauseWorkerGroup();
  const resumeGroup = useResumeWorkerGroup();

  const handleToggleGroupPause = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!groupId) return;
    if (groupPausedAt) {
      resumeGroup.mutate(groupId);
    } else {
      pauseGroup.mutate(groupId);
    }
  };

  return (
    <Panel className="overflow-hidden">
      <button
        className="w-full text-left px-4 py-3 hover:bg-panel-2/60 transition-colors"
        onClick={() => setExpanded(!expanded)}
      >
        <div className="flex items-center gap-3">
          {expanded ? <ChevronDown className="h-4 w-4 shrink-0" /> : <ChevronRight className="h-4 w-4 shrink-0" />}
          <span className={`inline-block w-2 h-2 rounded-full ${groupPausedAt ? 'bg-amber-500' : 'bg-green-500'}`} />
          <span className="font-medium text-sm">{workers.length} workers</span>
          {groupPausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300 text-xs">Paused</Badge>}
          {activeCount > 0 && (
            <span className="text-xs px-2 py-0.5 rounded-full font-medium bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300">
              {activeCount} active
            </span>
          )}
          <span className="text-xs text-text-mute">·</span>
          <span className="text-xs text-text-mute">Polling: {pollingMs >= 1000 ? `${(pollingMs / 1000).toFixed(pollingMs % 1000 === 0 ? 0 : 1)}s` : `${pollingMs}ms`}</span>
          <span className="ml-auto">
            {groupId && (
              <Button
                variant="ghost"
                size="sm"
                onClick={handleToggleGroupPause}
                disabled={pauseGroup.isPending || resumeGroup.isPending}
                title={groupPausedAt ? 'Resume group (takes effect on next heartbeat, ~3s)' : 'Pause group (takes effect on next heartbeat, ~3s)'}
                aria-label={groupPausedAt ? 'Resume worker group' : 'Pause worker group'}
                className="h-7 px-2"
              >
                {groupPausedAt ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
              </Button>
            )}
          </span>
        </div>
        <div className="ml-7 mt-1 text-xs text-text-mute">
          Queues: <span className="font-mono">{queues}</span>
          {groupPausedAt && <span className="ml-3">· Paused since <RelativeTime date={groupPausedAt} /></span>}
        </div>
      </button>

      {expanded && (
        <div className="overflow-x-auto border-t border-border">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Worker ID</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Current job</th>
              </tr>
            </thead>
            <tbody>
              {workers.map((w) => (
                <tr key={w.workerId} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                  <td className="px-3.5 py-2 font-mono text-[12.5px]">
                    <Link to={`/workers/${w.workerId}`} className="text-primary hover:underline">
                      {shortId(w.workerId)}
                    </Link>
                  </td>
                  <td className="px-3.5 py-2 text-[12.5px]">
                    {w.currentJobId ? (
                      <Link to={`/detail/${w.currentJobId}`} className="text-primary hover:underline font-mono">
                        {shortId(w.currentJobId)}
                      </Link>
                    ) : (
                      <span className="text-text-mute">Idle</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}

function TaskSection({ serverId, task }: { serverId: string; task: ServerTaskSummary }) {
  const [expanded, setExpanded] = useState(false);
  const [page, setPage] = useState(0);
  const logsQuery = useServerLogs(serverId, page, 10, task.taskName, expanded);
  const logs = logsQuery.data ?? null;

  return (
    <Panel className="overflow-hidden">
      <button
        className="w-full text-left px-4 py-3 flex items-center justify-between hover:bg-panel-2/60 transition-colors"
        onClick={() => setExpanded(!expanded)}
      >
        <div className="flex items-center gap-3 min-w-0">
          {expanded ? <ChevronDown className="h-4 w-4 shrink-0" /> : <ChevronRight className="h-4 w-4 shrink-0" />}
          <span className="font-medium text-sm">{task.taskName}</span>
          {task.lastStatus && (
            <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${statusColors[task.lastStatus] ?? 'text-text-mute'}`}>
              {task.lastStatus}
            </span>
          )}
        </div>
        <div className="flex items-center gap-4 text-xs text-text-mute shrink-0 whitespace-nowrap">
          {task.intervalSeconds != null ? (
            <span>every {task.intervalSeconds}s</span>
          ) : (
            <span className="text-yellow-600 dark:text-yellow-400 font-medium">Disabled</span>
          )}
          {task.lastDurationMs != null && <span>took {task.lastDurationMs.toFixed(0)}ms</span>}
          {task.lastRun && <span>ran <RelativeTime date={task.lastRun} /></span>}
        </div>
      </button>

      {expanded && (
        <div className="border-t border-border">
          {task.lastMessage && (
            <p className="px-4 pt-3 text-sm text-text-mute">{task.lastMessage}</p>
          )}
          {logs && logs.items.length > 0 ? (
            <>
              <div className="overflow-x-auto">
                <table className="w-full border-collapse">
                  <thead>
                    <tr className="bg-panel-2 border-b border-border">
                      <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Status</th>
                      <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Message</th>
                      <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Duration</th>
                      <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Time</th>
                    </tr>
                  </thead>
                  <tbody>
                    {logs.items.map((log) => (
                      <tr key={log.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                        <td className="px-3.5 py-2 text-[12.5px]">
                          <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${statusColors[log.status] ?? 'text-text-mute'}`}>
                            {log.status}
                          </span>
                        </td>
                        <td className="px-3.5 py-2 text-[12.5px] text-text-mute max-w-[300px] truncate">{log.message ?? '-'}</td>
                        <td className="px-3.5 py-2 text-[12.5px] text-text-mute">{log.durationMs != null ? `${log.durationMs.toFixed(0)}ms` : '-'}</td>
                        <td className="px-3.5 py-2 text-[12.5px]"><RelativeTime date={log.timestamp} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {logs.pageCount > 1 && (
                <Pagination page={page} pageCount={logs.pageCount} onPageChange={setPage} />
              )}
            </>
          ) : (
            <p className="text-text-mute text-sm py-2 text-center">{logs ? 'No logs yet' : 'Loading...'}</p>
          )}
        </div>
      )}
    </Panel>
  );
}
