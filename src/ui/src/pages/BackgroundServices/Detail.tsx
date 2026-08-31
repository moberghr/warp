import { useState, useEffect, useCallback, useRef } from 'react';
import { useParams, Link } from 'react-router-dom';
import axios from 'axios';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import {
  ServiceScope,
  BackgroundServiceStatus,
  BackgroundServiceLogSource,
  LogLevel,
} from '@/types/backgroundServices';
import type {
  BackgroundServiceDetail,
  BackgroundServiceLeaseDto,
  BackgroundServiceLogDto,
  BackgroundServiceInstance,
} from '@/types/backgroundServices';
import type { GetBackgroundServiceLogsOptions } from '@/api/backgroundServices';
import * as api from '@/api';
import { Hint } from '@/components/ui/tooltip';

export default function BackgroundServiceDetail() {
  const { name } = useParams<{ name: string }>();
  const decodedName = name ? decodeURIComponent(name) : '';

  const [detail, setDetail] = useState<BackgroundServiceDetail | null>(null);
  const [lease, setLease] = useState<BackgroundServiceLeaseDto | null>(null);
  const [logs, setLogs] = useState<BackgroundServiceLogDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [gone, setGone] = useState(false);
  const [activeTabIndex, setActiveTabIndex] = useState(0);

  // Log filter state
  const [sourceFilter, setSourceFilter] = useState<BackgroundServiceLogSource | 0>(0);
  const [levelFilter, setLevelFilter] = useState<LogLevel | -1>(-1);

  // Track highest seen log id for incremental polling
  const maxLogIdRef = useRef<number>(0);

  const fetchDetail = useCallback(async () => {
    if (!decodedName) {
      return;
    }

    try {
      const d = await api.getBackgroundService(decodedName);
      if (d === null) {
        setGone(true);

        return;
      }

      setDetail(d);
      setError(null);
      setGone(false);

      // Only fetch lease for singletons
      if (d.declaredScope === ServiceScope.Singleton) {
        const l = await api.getBackgroundServiceLease(decodedName);
        setLease(l);
      }
    } catch (e) {
      if (axios.isAxiosError(e) && e.response?.status === 404) {
        setGone(true);

        return;
      }

      setError('Unable to load service detail');
    }
  }, [decodedName]);

  const fetchLogs = useCallback(async () => {
    if (!decodedName) {
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
        // with fromId it returns rows with id > fromId, also newest-first)
        setLogs(prev => [...newLogs, ...prev].slice(0, 500));
      }
    } catch {
      // Non-critical — log polling failures are silent
    }
  }, [decodedName, sourceFilter, levelFilter]);

  // Reset log cursor when filters change so we re-fetch from scratch
  const handleSourceFilterChange = (val: BackgroundServiceLogSource | 0) => {
    maxLogIdRef.current = 0;
    setLogs([]);
    setSourceFilter(val);
  };

  const handleLevelFilterChange = (val: LogLevel | -1) => {
    maxLogIdRef.current = 0;
    setLogs([]);
    setLevelFilter(val);
  };

  useEffect(() => {
    void fetchDetail();
    const detailInterval = setInterval(fetchDetail, 2000);

    return () => clearInterval(detailInterval);
  }, [fetchDetail]);

  useEffect(() => {
    void fetchLogs();
    const logsInterval = setInterval(fetchLogs, 2000);

    return () => clearInterval(logsInterval);
  }, [fetchLogs]);

  if (gone) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/services" className="text-sm text-muted-foreground hover:underline">← Services</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            This service could not be found.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (error) return <ErrorState message={error} />;
  if (!detail) return <LoadingState />;

  const isSingleton = detail.declaredScope === ServiceScope.Singleton;

  return (
    <div>
      <div className="mb-4">
        <Link to="/services" className="text-sm text-muted-foreground hover:underline">← Services</Link>
      </div>

      <div className="flex items-center gap-3 mb-4">
        <h1 className="text-2xl font-bold">{detail.name}</h1>
        <ScopeBadge scope={detail.declaredScope} />
      </div>

      {/* Header timestamps */}
      <Card className="mb-4">
        <CardContent className="py-3 flex gap-8 text-sm flex-wrap">
          <div>
            <span className="text-muted-foreground mr-2">First seen:</span>
            <RelativeTime date={detail.firstSeenAt} />
          </div>
          <div>
            <span className="text-muted-foreground mr-2">Last seen:</span>
            <RelativeTime date={detail.lastSeenAt} />
          </div>
          <div>
            <span className="text-muted-foreground mr-2">Instances:</span>
            <span>{detail.instances.length}</span>
          </div>
        </CardContent>
      </Card>

      {/* Per-instance tabs */}
      {detail.instances.length > 0 && (
        <Card className="mb-4">
          <CardHeader className="pb-0">
            <CardTitle className="text-base">Instances</CardTitle>
          </CardHeader>

          {/* Tab list */}
          <div className="border-b px-4 flex gap-1 overflow-x-auto">
            {detail.instances.map((inst, idx) => (
              <button
                key={inst.serverId}
                type="button"
                onClick={() => setActiveTabIndex(idx)}
                className={`px-3 py-2 text-sm font-medium whitespace-nowrap border-b-2 transition-colors ${
                  idx === activeTabIndex
                    ? 'border-primary text-foreground'
                    : 'border-transparent text-muted-foreground hover:text-foreground'
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
            <CardContent className="pt-4">
              <InstancePanel instance={detail.instances[activeTabIndex]} />
            </CardContent>
          )}
        </Card>
      )}

      {/* Lease panel — singleton only */}
      {isSingleton && (
        <Card className="mb-4">
          <CardHeader><CardTitle className="text-base">Lease</CardTitle></CardHeader>
          <CardContent className="text-sm space-y-1">
            {lease ? (
              <>
                <div>
                  <span className="text-muted-foreground inline-block w-36">Holder</span>
                  <span>{lease.holderServerName ?? '(unknown)'}</span>
                </div>
                <div>
                  <span className="text-muted-foreground inline-block w-36">Holder server ID</span>
                  <span className="font-mono text-xs">{lease.holderServerId}</span>
                </div>
                <div>
                  <span className="text-muted-foreground inline-block w-36">Expires</span>
                  <LeaseCountdown expiresAt={lease.leaseExpiresAt} />
                </div>
              </>
            ) : (
              <span className="text-muted-foreground">No active lease — service is waiting for a holder.</span>
            )}
          </CardContent>
        </Card>
      )}

      {/* Log tail */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between flex-wrap gap-2">
            <CardTitle className="text-base">Logs</CardTitle>
            <div className="flex gap-2">
              <select
                className="border rounded-md px-2 py-1 text-xs bg-background"
                value={sourceFilter}
                onChange={(e) => handleSourceFilterChange(Number(e.target.value) as BackgroundServiceLogSource | 0)}
              >
                <option value={0}>All sources</option>
                <option value={BackgroundServiceLogSource.Lifecycle}>Lifecycle</option>
                <option value={BackgroundServiceLogSource.User}>User</option>
              </select>
              <select
                className="border rounded-md px-2 py-1 text-xs bg-background"
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
          </div>
        </CardHeader>
        <CardContent className="p-0">
          {logs.length === 0 ? (
            <div className="py-8 text-center text-muted-foreground text-sm">No logs captured yet</div>
          ) : (
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
                {logs.map((log) => (
                  <LogRow key={log.id} log={log} />
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function InstancePanel({ instance }: { instance: BackgroundServiceInstance }) {
  return (
    <div className="space-y-1.5 text-sm">
      <div>
        <span className="text-muted-foreground inline-block w-36">Server</span>
        <span>{instance.serverName ?? '(unknown)'}</span>
      </div>
      <div>
        <span className="text-muted-foreground inline-block w-36">Server ID</span>
        <span className="font-mono text-xs">{instance.serverId}</span>
      </div>
      <div>
        <span className="text-muted-foreground inline-block w-36">Status</span>
        <StatusBadge status={instance.status} />
      </div>
      <div>
        <span className="text-muted-foreground inline-block w-36">Started</span>
        <RelativeTime date={instance.startedAt} />
      </div>
      <div>
        <span className="text-muted-foreground inline-block w-36">Last heartbeat</span>
        <RelativeTime date={instance.lastHeartbeatAt} />
      </div>
      <div>
        <span className="text-muted-foreground inline-block w-36">Restart count</span>
        {instance.restartCount > 0 ? (
          <span className="text-amber-600 dark:text-amber-400">{instance.restartCount}</span>
        ) : (
          <span>0</span>
        )}
      </div>
      {instance.lastError && (
        <>
          <div>
            <span className="text-muted-foreground inline-block w-36">Last error at</span>
            {instance.lastErrorAt ? <RelativeTime date={instance.lastErrorAt} /> : '—'}
          </div>
          <div>
            <span className="text-muted-foreground inline-block w-36 align-top">Last error</span>
            <pre className="inline-block align-top text-xs font-mono bg-red-50 dark:bg-red-950/20 text-red-800 dark:text-red-300 rounded-md p-2 whitespace-pre-wrap max-w-2xl overflow-auto max-h-48">
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
        <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
          {formatTs(log.timestamp)}
        </TableCell>
        <Hint text={log.serverId}>
          <TableCell className="text-xs text-muted-foreground truncate">
            {log.serverName ?? shortServerId(log.serverId)}
          </TableCell>
        </Hint>
        <TableCell>
          <LevelBadge level={log.level} />
        </TableCell>
        <TableCell className="text-xs text-muted-foreground">
          {log.source === BackgroundServiceLogSource.Lifecycle ? 'Lifecycle' : 'User'}
        </TableCell>
        <TableCell className="text-sm">
          {log.message}
          {hasException && (
            <span className="ml-2 text-xs text-muted-foreground">{expanded ? '▲' : '▼'} exception</span>
          )}
        </TableCell>
      </TableRow>
      {expanded && hasException && (
        <TableRow>
          <TableCell colSpan={5} className="bg-red-50 dark:bg-red-950/10 px-4 py-2">
            {log.exceptionType && (
              <div className="text-xs font-mono text-red-700 dark:text-red-400 font-semibold mb-1">{log.exceptionType}</div>
            )}
            {log.exceptionMessage && (
              <pre className="text-xs font-mono text-red-800 dark:text-red-300 whitespace-pre-wrap">{log.exceptionMessage}</pre>
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
      ? 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400'
      : 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';

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
    status === BackgroundServiceStatus.Running ? 'bg-green-500' :
    status === BackgroundServiceStatus.Waiting ? 'bg-yellow-400' :
    status === BackgroundServiceStatus.Faulted ? 'bg-red-500' :
    status === BackgroundServiceStatus.Restarting ? 'bg-amber-500 animate-pulse' :
    'bg-gray-400';

  return <span className={`inline-block w-2 h-2 rounded-full ${dotCls}`} />;
}

function statusStyle(status: number): { label: string; cls: string } {
  switch (status) {
    case BackgroundServiceStatus.Running:
      return { label: 'Running', cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' };
    case BackgroundServiceStatus.Waiting:
      return { label: 'Waiting', cls: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400' };
    case BackgroundServiceStatus.Faulted:
      return { label: 'Faulted', cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' };
    case BackgroundServiceStatus.Restarting:
      return { label: 'Restarting', cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400' };
    case BackgroundServiceStatus.ConfigurationMismatch:
      return { label: 'Mismatch', cls: 'bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-400' };
    default:
      return { label: 'Unknown', cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400' };
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
      return { label: level === LogLevel.Trace ? 'Trace' : 'Debug', cls: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400' };
    case LogLevel.Information:
      return { label: 'Info', cls: 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400' };
    case LogLevel.Warning:
      return { label: 'Warn', cls: 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400' };
    case LogLevel.Error:
      return { label: 'Error', cls: 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400' };
    case LogLevel.Critical:
      return { label: 'Critical', cls: 'bg-red-200 text-red-900 dark:bg-red-900/50 dark:text-red-300 font-bold' };
    default:
      return { label: 'None', cls: 'bg-gray-100 text-gray-400 dark:bg-gray-800' };
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
      <span className={expired ? 'text-red-600 dark:text-red-400 font-medium' : 'text-muted-foreground'}>
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
