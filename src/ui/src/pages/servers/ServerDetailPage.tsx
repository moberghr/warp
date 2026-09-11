import { useState, useEffect, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { shortId, formatBytes } from '@/utils/format';
import { useHeartbeatStale, useServerStatusDotColor } from '@/hooks/useTicking';
import { ChevronDown, ChevronRight, RefreshCw, Pause, Play } from 'lucide-react';
import type { ServerModel, WorkerModel, ServerTaskSummary, ServerLogModel, PagedList } from '@/types';
import * as api from '@/api';
import { Hint } from '@/components/ui/tooltip';

const statusColors: Record<string, string> = {
  Completed: 'text-green-700 dark:text-green-400 bg-green-50 dark:bg-green-950/30',
  Failed: 'text-red-700 dark:text-red-400 bg-red-50 dark:bg-red-950/30',
};

export default function ServerDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [server, setServer] = useState<ServerModel | null>(null);
  const [tasks, setTasks] = useState<ServerTaskSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  const fetchData = useCallback(() => {
    if (id) {
      // Clearing the error on success matters now that this polls: the error state replaces the whole
      // page, including its Refresh button, so without this one transient failure would strand the
      // page on <ErrorState/> while the interval kept fetching successfully behind it.
      api.getServerById(id)
        .then((x) => {
          setServer(x);
          setError(null);
        })
        .catch(() => setError('Unable to load server'));
      api.getServerTaskSummaries(id).then(setTasks).catch(() => {});
    }
  }, [id]);

  // Same 10s cadence as useServers()/useServerDetail(). This page is a live status readout — CPU,
  // memory, task history — and it was fetching once and then only on the manual refresh button.
  // Keyed on fetchData rather than driven by usePolling so switching to another server's page,
  // which changes the route param without remounting, still fetches immediately.
  useEffect(() => {
    fetchData();
    const timer = setInterval(fetchData, 10_000);

    return () => clearInterval(timer);
  }, [fetchData]);

  const handleTogglePause = async () => {
    if (!server || !id) return;
    if (server.pausedAt) {
      await api.resumeServer(id);
    } else {
      await api.pauseServer(id);
    }
    fetchData();
  };

  // Only when there is nothing to show. Now that this polls, replacing a loaded page with the error
  // state every 10s through a rolling restart would unmount every TaskSection on each flip, losing
  // whatever the operator had expanded and whichever page of server logs they were reading.
  if (error && !server) return <ErrorState message={error} />;
  if (!server) return <LoadingState />;

  return (
    <div>
      {error && (
        <p className="mb-4 text-sm text-amber-600 dark:text-amber-400">{error} — showing the last successful read.</p>
      )}

      <div className="flex items-center gap-4 mb-6">
        <StatusDot lastHeartbeatTime={server.lastHeartbeatTime} pausedAt={server.pausedAt} />
        <h1 className="text-2xl font-bold">{server.serverName}</h1>
        {server.pausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Paused</Badge>}
        <InactiveBadge lastHeartbeatTime={server.lastHeartbeatTime} />
        <Hint text="Refresh">
          <button onClick={fetchData} className="p-2 rounded-md hover:bg-accent text-muted-foreground" aria-label="Refresh">
            <RefreshCw className="h-4 w-4" />
          </button>
        </Hint>
        <Hint text={server.pausedAt ? 'Resume server' : 'Pause server'}>
          <Button variant="outline" size="sm" onClick={handleTogglePause}>
            {server.pausedAt ? <><Play className="h-4 w-4 mr-1" /> Resume</> : <><Pause className="h-4 w-4 mr-1" /> Pause</>}
          </Button>
        </Hint>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6">
        {/* Details */}
        <Card>
          <CardHeader className="pb-2"><CardTitle className="text-sm">Details</CardTitle></CardHeader>
          <CardContent className="space-y-2 text-sm">
            <div><span className="text-muted-foreground">Workers:</span> {server.serviceCount}</div>
            <div><span className="text-muted-foreground">CPU:</span> {server.cpuUsagePercent != null ? `${server.cpuUsagePercent}%` : 'N/A'}</div>
            <div><span className="text-muted-foreground">Memory:</span> {server.memoryWorkingSetBytes != null ? formatBytes(server.memoryWorkingSetBytes) : 'N/A'}</div>
            <div><span className="text-muted-foreground">Started:</span> <RelativeTime date={server.startedTime} /></div>
            <div><span className="text-muted-foreground">Heartbeat:</span> <RelativeTime date={server.lastHeartbeatTime} /></div>
            {server.pausedAt && (
              <div><span className="text-muted-foreground">Paused since:</span> <RelativeTime date={server.pausedAt} /></div>
            )}
            <div><span className="text-muted-foreground">ID:</span> <span className="font-mono text-xs">{server.id}</span></div>
          </CardContent>
        </Card>

      </div>

      {/* Worker Groups */}
      <h2 className="text-lg font-semibold mb-3">Worker Groups</h2>
      {(() => {
        // Group by workerGroupId when available, fall back to queues|pollingMs
        const groups = new Map<string, WorkerModel[]>();
        for (const w of server.workers) {
          const key = w.workerGroupId ?? `${w.queues ?? 'default'}|${w.pollingIntervalMs ?? 1000}`;
          if (!groups.has(key)) groups.set(key, []);
          groups.get(key)!.push(w);
        }
        return groups.size > 0 ? (
          <div className="space-y-2 mb-6">
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
                  onTogglePause={fetchData}
                />
              );
            })}
          </div>
        ) : (
          <Card className="mb-6">
            <CardContent className="py-6 text-center text-muted-foreground text-sm">No workers registered</CardContent>
          </Card>
        );
      })()}

      {/* Task Sections */}
      <h2 className="text-lg font-semibold mb-3">Server Tasks</h2>
      {tasks.length === 0 ? (
        <Card>
          <CardContent className="py-6 text-center text-muted-foreground text-sm">No task logs yet</CardContent>
        </Card>
      ) : (
        <div className="space-y-2">
          {tasks.map((task) => (
            <TaskSection key={task.taskName} serverId={server.id} task={task} />
          ))}
        </div>
      )}
    </div>
  );
}

function WorkerGroupSection({ queues, pollingMs, workers, activeCount, groupId, groupPausedAt, onTogglePause }: {
  queues: string;
  pollingMs: number;
  workers: WorkerModel[];
  activeCount: number;
  groupId: string | null;
  groupPausedAt: string | null;
  onTogglePause: () => void;
}) {
  const [expanded, setExpanded] = useState(false);

  const handleToggleGroupPause = async (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!groupId) return;
    if (groupPausedAt) {
      await api.resumeWorkerGroup(groupId);
    } else {
      await api.pauseWorkerGroup(groupId);
    }
    onTogglePause();
  };

  return (
    <Card>
      <button
        className="w-full text-left px-4 py-3 hover:bg-accent/50 rounded-t-lg transition-colors"
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
          <span className="text-xs text-muted-foreground">·</span>
          <span className="text-xs text-muted-foreground">Polling: {pollingMs >= 1000 ? `${(pollingMs / 1000).toFixed(pollingMs % 1000 === 0 ? 0 : 1)}s` : `${pollingMs}ms`}</span>
          <span className="ml-auto">
            {groupId && (
              <Hint text={groupPausedAt ? 'Resume group' : 'Pause group'}>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={handleToggleGroupPause}
                  aria-label={groupPausedAt ? 'Resume group' : 'Pause group'}
                  className="h-7 px-2"
                >
                  {groupPausedAt ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
                </Button>
              </Hint>
            )}
          </span>
        </div>
        <div className="ml-7 mt-1 text-xs text-muted-foreground">
          Queues: <span className="font-mono">{queues}</span>
          {groupPausedAt && <span className="ml-3">· Paused since <RelativeTime date={groupPausedAt} /></span>}
        </div>
      </button>

      {expanded && (
        <CardContent className="pt-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Worker ID</TableHead>
                <TableHead>Current Job</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {workers.map((w) => (
                <TableRow key={w.workerId}>
                  <TableCell className="font-mono text-xs">
                    <Link to={`/workers/${w.workerId}`} className="text-primary hover:underline">
                      {shortId(w.workerId)}
                    </Link>
                  </TableCell>
                  <TableCell>
                    {w.currentJobId ? (
                      <Link to={`/detail/${w.currentJobId}`} className="text-primary hover:underline text-xs font-mono">
                        {shortId(w.currentJobId)}
                      </Link>
                    ) : (
                      <span className="text-muted-foreground text-sm">Idle</span>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      )}
    </Card>
  );
}

function TaskSection({ serverId, task }: { serverId: string; task: ServerTaskSummary }) {
  const [expanded, setExpanded] = useState(false);
  const [logs, setLogs] = useState<PagedList<ServerLogModel> | null>(null);
  const [page, setPage] = useState(0);

  const fetchLogs = useCallback(async () => {
    if (!expanded) return;
    try {
      const result = await api.getServerLogs(serverId, page, 10, task.taskName);
      setLogs(result);
    } catch {
      // Non-critical
    }
  }, [serverId, task.taskName, page, expanded]);

  useEffect(() => { fetchLogs(); }, [fetchLogs]);

  return (
    <Card>
      <button
        className="w-full text-left px-4 py-3 flex items-center justify-between hover:bg-accent/50 rounded-t-lg transition-colors"
        onClick={() => setExpanded(!expanded)}
      >
        <div className="flex items-center gap-3 min-w-0">
          {expanded ? <ChevronDown className="h-4 w-4 shrink-0" /> : <ChevronRight className="h-4 w-4 shrink-0" />}
          <span className="font-medium text-sm">{task.taskName}</span>
          {task.lastStatus && (
            <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${statusColors[task.lastStatus] ?? 'text-muted-foreground'}`}>
              {task.lastStatus}
            </span>
          )}
        </div>
        <div className="flex items-center gap-4 text-xs text-muted-foreground shrink-0 whitespace-nowrap">
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
        <CardContent className="pt-0">
          {task.lastMessage && (
            <p className="text-sm text-muted-foreground mb-3">{task.lastMessage}</p>
          )}
          {logs && logs.items.length > 0 ? (
            <>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Status</TableHead>
                    <TableHead>Message</TableHead>
                    <TableHead>Duration</TableHead>
                    <TableHead>Time</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {logs.items.map((log) => (
                    <TableRow key={log.id}>
                      <TableCell>
                        <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${statusColors[log.status] ?? 'text-muted-foreground'}`}>
                          {log.status}
                        </span>
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground max-w-[300px] truncate">{log.message ?? '-'}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">{log.durationMs != null ? `${log.durationMs.toFixed(0)}ms` : '-'}</TableCell>
                      <TableCell className="text-sm"><RelativeTime date={log.timestamp} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              {logs.pageCount > 1 && (
                <Pagination page={page} pageCount={logs.pageCount} onPageChange={setPage} />
              )}
            </>
          ) : (
            <p className="text-muted-foreground text-sm py-2 text-center">{logs ? 'No logs yet' : 'Loading...'}</p>
          )}
        </CardContent>
      )}
    </Card>
  );
}

// Leaves, so the clock re-renders a dot and a badge rather than this whole page. Both read the
// dashboard's own 30s stale threshold and re-evaluate it every second: a server that stops checking
// in goes red while you are looking at it, without waiting for a poll to bring the same timestamp
// back and re-render it by accident.
function StatusDot({ lastHeartbeatTime, pausedAt }: { lastHeartbeatTime: string; pausedAt: string | null }) {
  const color = useServerStatusDotColor(lastHeartbeatTime, pausedAt);

  return <span className={`inline-block w-3 h-3 rounded-full ${color}`} />;
}

function InactiveBadge({ lastHeartbeatTime }: { lastHeartbeatTime: string }) {
  const stale = useHeartbeatStale(lastHeartbeatTime);

  if (!stale) {
    return null;
  }

  return <Badge variant="outline" className="text-red-600 border-red-300">Inactive</Badge>;
}
