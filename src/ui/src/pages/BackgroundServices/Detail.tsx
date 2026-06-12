import { useState, useEffect, useCallback, useRef } from 'react';
import { useParams, Link } from 'react-router-dom';
import axios from 'axios';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import {
  ServiceScope,
  BackgroundServiceStatus,
  BackgroundServiceLogSource,
  LogLevel,
} from '@/types/backgroundServices';
import type {
  BackgroundServiceInstance,
  BackgroundServiceLogDto,
} from '@/types/backgroundServices';
import type { GetBackgroundServiceLogsOptions } from '@/api/backgroundServices';
import { usePageStore } from '@/stores/page';
import { useBackgroundServiceDetail, useBackgroundServiceLease } from '@/api/hooks/useBackgroundServices';
import * as api from '@/api';

const LOG_PAGE_SIZE = 50;

export default function BackgroundServiceDetail() {
  const { name } = useParams<{ name: string }>();
  const decodedName = name ? decodeURIComponent(name) : '';

  const [logs, setLogs] = useState<BackgroundServiceLogDto[]>([]);
  const [activeTabIndex, setActiveTabIndex] = useState(0);

  // Log filter state
  const [sourceFilter, setSourceFilter] = useState<BackgroundServiceLogSource | 0>(0);
  const [levelFilter, setLevelFilter] = useState<LogLevel | -1>(-1);
  const [logPage, setLogPage] = useState(0);

  // Track highest seen log id for incremental polling
  const maxLogIdRef = useRef<number>(0);

  // Drive the shared topbar header; without this the previous page's header
  // (and its action buttons) would keep rendering above this page.
  useEffect(() => {
    usePageStore.getState().set({
      title: decodedName || 'Background Service',
      subtitle: 'Background service',
    });
    return () => usePageStore.getState().reset();
  }, [decodedName]);

  const detailQuery = useBackgroundServiceDetail(decodedName || undefined);
  const detail = detailQuery.data ?? null;
  const isSingleton = detail?.declaredScope === ServiceScope.Singleton;
  const leaseQuery = useBackgroundServiceLease(decodedName || undefined, isSingleton);
  const lease = leaseQuery.data ?? null;

  // The service was renamed/removed (orphan cleanup), or the name is unknown.
  const gone =
    (detailQuery.isSuccess && detailQuery.data === null) ||
    (axios.isAxiosError(detailQuery.error) && detailQuery.error.response?.status === 404);

  // Log tail is an incremental stream (id > fromId, prepend, 500-row cap), so it
  // stays outside React Query — request/response caching doesn't fit accumulation.
  const fetchLogs = useCallback(async () => {
    if (!decodedName) {
      return;
    }

    // Pause incremental prepends while the user is browsing older pages so the
    // visible rows don't shift under them. Polling resumes once they return to
    // page 0.
    if (logPage !== 0 && maxLogIdRef.current > 0) {
      return;
    }

    try {
      const opts: GetBackgroundServiceLogsOptions = { limit: 100 };
      if (sourceFilter !== 0) {
        opts.source = sourceFilter;
      }
      if (levelFilter !== -1) {
        opts.minLevel = levelFilter;
      }
      if (maxLogIdRef.current > 0) {
        opts.fromId = maxLogIdRef.current;
      }

      const newLogs = await api.getBackgroundServiceLogs(decodedName, opts);
      if (newLogs.length > 0) {
        const newMax = Math.max(...newLogs.map(l => l.id));
        maxLogIdRef.current = newMax;
        // Prepend new logs (they are newer — API returns newest-first when no fromId;
        // with fromId it returns rows with id > fromId, also newest-first). Drop any
        // ids we already hold so an overlapping window can't duplicate rows.
        setLogs(prev => {
          const incoming = new Set(newLogs.map(l => l.id));
          return [...newLogs, ...prev.filter(l => !incoming.has(l.id))].slice(0, 500);
        });
      }
    } catch {
      // Non-critical — log polling failures are silent
    }
  }, [decodedName, sourceFilter, levelFilter, logPage]);

  // Reset log cursor when filters change so we re-fetch from scratch
  const handleSourceFilterChange = (val: BackgroundServiceLogSource | 0) => {
    maxLogIdRef.current = 0;
    setLogs([]);
    setLogPage(0);
    setSourceFilter(val);
  };

  const handleLevelFilterChange = (val: LogLevel | -1) => {
    maxLogIdRef.current = 0;
    setLogs([]);
    setLogPage(0);
    setLevelFilter(val);
  };

  useEffect(() => {
    void fetchLogs();
    const logsInterval = setInterval(() => {
      if (document.visibilityState === 'hidden') {
        return;
      }
      void fetchLogs();
    }, 2000);

    return () => clearInterval(logsInterval);
  }, [fetchLogs]);

  if (gone) {
    return (
      <div className="flex flex-col gap-3 py-5">
        <div>
          <Link to="/services" className="text-sm text-text-mute hover:underline">← Services</Link>
        </div>
        <Panel>
          <div className="py-8 text-center text-[13px] text-text-mute">
            This service could not be found.
          </div>
        </Panel>
      </div>
    );
  }

  if (detailQuery.error) return <ErrorState message="Unable to load service detail" />;
  if (!detail) return <LoadingState />;

  return (
    <div className="flex flex-col gap-3 py-5">
      <div className="flex items-center justify-between gap-3">
        <Link to="/services" className="text-sm text-text-mute hover:underline">← Services</Link>
        <ScopeBadge scope={detail.declaredScope} />
      </div>

      {/* Header timestamps */}
      <Panel>
        <div className="px-4 py-3 flex gap-8 text-[13px] flex-wrap">
          <div>
            <span className="text-text-mute mr-2">First seen:</span>
            <RelativeTime date={detail.firstSeenAt} />
          </div>
          <div>
            <span className="text-text-mute mr-2">Last seen:</span>
            <RelativeTime date={detail.lastSeenAt} />
          </div>
          <div>
            <span className="text-text-mute mr-2">Instances:</span>
            <span>{detail.instances.length}</span>
          </div>
        </div>
      </Panel>

      {/* Per-instance tabs */}
      {detail.instances.length > 0 && (
        <Panel>
          <PanelHeader eyebrow="Instances" />

          {/* Tab list */}
          <div className="border-b border-border px-4 flex gap-1 overflow-x-auto">
            {detail.instances.map((inst, idx) => (
              <button
                key={inst.serverId}
                type="button"
                onClick={() => setActiveTabIndex(idx)}
                className={`px-3 py-2 text-sm font-medium whitespace-nowrap border-b-2 transition-colors ${
                  idx === activeTabIndex
                    ? 'border-primary text-foreground'
                    : 'border-transparent text-text-mute hover:text-foreground'
                }`}
              >
                <span className="flex items-center gap-1.5">
                  <StatusDot status={inst.status} />
                  {inst.serverName ?? shortServerId(inst.serverId)}
                </span>
              </button>
            ))}
          </div>

          {/* Tab panel */}
          {detail.instances[activeTabIndex] && (
            <div className="px-4 py-4">
              <InstancePanel instance={detail.instances[activeTabIndex]} />
            </div>
          )}
        </Panel>
      )}

      {/* Lease panel — singleton only */}
      {isSingleton && (
        <Panel>
          <PanelHeader eyebrow="Lease" />
          <div className="px-4 py-3 text-[13px] space-y-1">
            {lease ? (
              <>
                <div>
                  <span className="text-text-mute inline-block w-36">Holder</span>
                  <span>{lease.holderServerName ?? '(unknown)'}</span>
                </div>
                <div>
                  <span className="text-text-mute inline-block w-36">Holder server ID</span>
                  <span className="font-mono text-xs">{lease.holderServerId}</span>
                </div>
                <div>
                  <span className="text-text-mute inline-block w-36">Expires</span>
                  <LeaseCountdown expiresAt={lease.leaseExpiresAt} />
                </div>
              </>
            ) : (
              <span className="text-text-mute">No active lease — service is waiting for a holder.</span>
            )}
          </div>
        </Panel>
      )}

      {/* Log tail */}
      <Panel className="overflow-hidden">
        <PanelHeader
          eyebrow="Logs"
          action={
            <div className="flex items-center gap-2">
              {logPage !== 0 && (
                <span className="text-[11px] text-warp-amber">live updates paused</span>
              )}
              <select
                className="border border-border rounded-md px-2 py-1 text-xs bg-background"
                aria-label="Filter logs by source"
                value={sourceFilter}
                onChange={(e) => handleSourceFilterChange(Number(e.target.value) as BackgroundServiceLogSource | 0)}
              >
                <option value={0}>All sources</option>
                <option value={BackgroundServiceLogSource.Lifecycle}>Lifecycle</option>
                <option value={BackgroundServiceLogSource.User}>User</option>
              </select>
              <select
                className="border border-border rounded-md px-2 py-1 text-xs bg-background"
                aria-label="Filter logs by level"
                value={levelFilter}
                onChange={(e) => handleLevelFilterChange(Number(e.target.value) as LogLevel | -1)}
              >
                <option value={-1}>All levels</option>
                <option value={LogLevel.Information}>Information+</option>
                <option value={LogLevel.Warning}>Warning+</option>
                <option value={LogLevel.Error}>Error+</option>
                <option value={LogLevel.Critical}>Critical</option>
              </select>
            </div>
          }
        />
        {logs.length === 0 ? (
          <div className="py-8 text-center text-text-mute text-sm">
            No logs captured yet
            <div className="text-xs text-text-mute mt-1">
              Logs are rate-capped (100/s) and capped to 1,000 rows or 7 days per service; recent events may be filtered.
            </div>
          </div>
        ) : (
          <>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-44">Timestamp</TableHead>
                  <TableHead className="w-40">Server</TableHead>
                  <TableHead className="w-24">Level</TableHead>
                  <TableHead className="w-24">Source</TableHead>
                  <TableHead>Message</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {logs
                  .slice(logPage * LOG_PAGE_SIZE, (logPage + 1) * LOG_PAGE_SIZE)
                  .map((log) => (
                    <LogRow key={log.id} log={log} />
                  ))}
              </TableBody>
            </Table>
            <Pagination
              page={logPage}
              pageCount={Math.max(1, Math.ceil(logs.length / LOG_PAGE_SIZE))}
              onPageChange={setLogPage}
              pageSize={LOG_PAGE_SIZE}
              totalCount={logs.length}
              className="px-3.5 py-2.5 mt-0 border-t border-border"
            />
          </>
        )}
      </Panel>
    </div>
  );
}

function InstancePanel({ instance }: { instance: BackgroundServiceInstance }) {
  return (
    <div className="space-y-1.5 text-[13px]">
      <div>
        <span className="text-text-mute inline-block w-36">Server</span>
        <span>{instance.serverName ?? '(unknown)'}</span>
      </div>
      <div>
        <span className="text-text-mute inline-block w-36">Server ID</span>
        <span className="font-mono text-xs">{instance.serverId}</span>
      </div>
      <div>
        <span className="text-text-mute inline-block w-36">Status</span>
        <StatusBadge status={instance.status} />
      </div>
      <div>
        <span className="text-text-mute inline-block w-36">Started</span>
        <RelativeTime date={instance.startedAt} />
      </div>
      <div>
        <span className="text-text-mute inline-block w-36">Last heartbeat</span>
        <RelativeTime date={instance.lastHeartbeatAt} />
      </div>
      <div>
        <span className="text-text-mute inline-block w-36">Restart count</span>
        {instance.restartCount > 0 ? (
          <span className="text-warp-amber">{instance.restartCount}</span>
        ) : (
          <span>0</span>
        )}
      </div>
      {instance.lastError && (
        <>
          <div>
            <span className="text-text-mute inline-block w-36">Last error at</span>
            {instance.lastErrorAt ? <RelativeTime date={instance.lastErrorAt} /> : '—'}
          </div>
          <div>
            <span className="text-text-mute inline-block w-36 align-top">Last error</span>
            <pre className="inline-block align-top text-xs font-mono bg-warp-red-soft text-warp-red rounded-md p-2 whitespace-pre-wrap max-w-2xl overflow-auto max-h-48">
              {instance.lastError}
            </pre>
          </div>
        </>
      )}
    </div>
  );
}

function LogRow({ log }: { log: BackgroundServiceLogDto }) {
  const [expanded, setExpanded] = useState(false);
  const hasException = !!(log.exceptionType || log.exceptionMessage);

  return (
    <>
      <TableRow
        className={hasException ? 'cursor-pointer hover:bg-accent/30' : ''}
        onClick={hasException ? () => setExpanded(!expanded) : undefined}
      >
        <TableCell className="text-xs text-text-mute whitespace-nowrap">
          {formatTs(log.timestamp)}
        </TableCell>
        <TableCell className="text-xs text-text-mute truncate" title={log.serverId}>
          {log.serverName ?? shortServerId(log.serverId)}
        </TableCell>
        <TableCell>
          <LevelBadge level={log.level} />
        </TableCell>
        <TableCell className="text-xs text-text-mute">
          {log.source === BackgroundServiceLogSource.Lifecycle ? 'Lifecycle' : 'User'}
        </TableCell>
        <TableCell className="text-sm">
          {log.message}
          {hasException && (
            <span className="ml-2 text-xs text-text-mute">{expanded ? '▲' : '▼'} exception</span>
          )}
        </TableCell>
      </TableRow>
      {expanded && hasException && (
        <TableRow>
          <TableCell colSpan={5} className="bg-warp-red-soft px-4 py-2">
            {log.exceptionType && (
              <div className="text-xs font-mono text-warp-red font-semibold mb-1">{log.exceptionType}</div>
            )}
            {log.exceptionMessage && (
              <pre className="text-xs font-mono text-warp-red whitespace-pre-wrap">{log.exceptionMessage}</pre>
            )}
          </TableCell>
        </TableRow>
      )}
    </>
  );
}

function ScopeBadge({ scope }: { scope: number }) {
  const label = scope === ServiceScope.Singleton ? 'Singleton' : 'Per Server';
  const cls =
    scope === ServiceScope.Singleton
      ? 'bg-warp-purple-soft text-warp-purple'
      : 'bg-warp-blue-soft text-warp-blue';

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>
      {label}
    </span>
  );
}

function StatusBadge({ status }: { status: number }) {
  const { label, cls } = statusStyle(status);

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>
      {label}
    </span>
  );
}

function StatusDot({ status }: { status: number }) {
  const dotCls =
    status === BackgroundServiceStatus.Running ? 'bg-warp-green' :
    status === BackgroundServiceStatus.Waiting ? 'bg-warp-amber' :
    status === BackgroundServiceStatus.Faulted ? 'bg-warp-red' :
    status === BackgroundServiceStatus.Restarting ? 'bg-warp-purple animate-pulse' :
    'bg-gray-400';

  return <span className={`inline-block w-2 h-2 rounded-full ${dotCls}`} />;
}

function statusStyle(status: number): { label: string; cls: string } {
  switch (status) {
    case BackgroundServiceStatus.Running:
      return { label: 'Running', cls: 'bg-warp-green-soft text-warp-green' };
    case BackgroundServiceStatus.Waiting:
      return { label: 'Waiting', cls: 'bg-warp-amber-soft text-warp-amber' };
    case BackgroundServiceStatus.Faulted:
      return { label: 'Faulted', cls: 'bg-warp-red-soft text-warp-red' };
    case BackgroundServiceStatus.Restarting:
      return { label: 'Restarting', cls: 'bg-warp-purple-soft text-warp-purple' };
    case BackgroundServiceStatus.ConfigurationMismatch:
      return { label: 'Mismatch', cls: 'bg-warp-amber-soft text-warp-amber' };
    default:
      return { label: 'Unknown', cls: 'bg-panel-2 text-text-mute' };
  }
}

function LevelBadge({ level }: { level: number }) {
  const { label, cls } = levelStyle(level);

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>
      {label}
    </span>
  );
}

function levelStyle(level: number): { label: string; cls: string } {
  switch (level) {
    case LogLevel.Trace:
    case LogLevel.Debug:
      return { label: level === LogLevel.Trace ? 'Trace' : 'Debug', cls: 'bg-panel-2 text-text-mute' };
    case LogLevel.Information:
      return { label: 'Info', cls: 'bg-warp-blue-soft text-warp-blue' };
    case LogLevel.Warning:
      return { label: 'Warn', cls: 'bg-warp-amber-soft text-warp-amber' };
    case LogLevel.Error:
      return { label: 'Error', cls: 'bg-warp-red-soft text-warp-red' };
    case LogLevel.Critical:
      return { label: 'Critical', cls: 'bg-warp-red-soft text-warp-red font-bold' };
    default:
      return { label: 'None', cls: 'bg-panel-2 text-text-mute' };
  }
}

function LeaseCountdown({ expiresAt }: { expiresAt: string }) {
  const [secsLeft, setSecsLeft] = useState(() => Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000));

  useEffect(() => {
    const interval = setInterval(() => {
      setSecsLeft(Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000));
    }, 1000);

    return () => clearInterval(interval);
  }, [expiresAt]);

  const expired = secsLeft <= 0;

  return (
    <span>
      <RelativeTime date={expiresAt} />
      {' '}
      <span className={expired ? 'text-warp-red font-medium' : 'text-text-mute'}>
        ({expired ? 'expired' : `in ${secsLeft}s`})
      </span>
    </span>
  );
}

function shortServerId(serverId: string): string {
  return serverId.length > 8 ? serverId.substring(0, 8) + '…' : serverId;
}

function formatTs(iso: string): string {
  try {
    return new Date(iso).toISOString().replace('T', ' ').substring(0, 23);
  } catch {
    return iso;
  }
}
