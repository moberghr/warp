import { useEffect, useMemo, useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { X, ChevronLeft, ChevronRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { PerformanceChart } from '@/components/PerformanceChart';
import * as api from '@/api';
import { HealthPill, adapterHealth, OutcomeBadge, HttpStatus, formatPercent, formatMs } from '../adapters/shared';
import { Hint } from '@/components/ui/tooltip';
import { DASHBOARD_LOCALE } from '@/utils/format';

const PAGE_SIZE = 15;

export default function EndpointDetailPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';
  const navigate = useNavigate();

  // Filter for the recent-calls list — driven by clicking a caller (group) row.
  const [groupFilter, setGroupFilter] = useState<string | null>(null);
  const [page, setPage] = useState(0);

  const detailQuery = useQuery({
    queryKey: ['endpoints', 'detail', id] as const,
    queryFn: () => api.getEndpointDetail(id),
    enabled: !!id,
  });

  const notFound =
    detailQuery.isError &&
    axios.isAxiosError(detailQuery.error) &&
    detailQuery.error.response?.status === 404;

  const detail = detailQuery.data;

  const filteredCalls = useMemo(() => {
    if (!detail) {
      return [];
    }

    return detail.recentCalls.filter((x) => groupFilter === null || x.groupName === groupFilter);
  }, [detail, groupFilter]);

  // Reset to the first page whenever the filter changes so the view never lands on an empty page.
  useEffect(() => {
    setPage(0);
  }, [groupFilter]);

  const pageCount = Math.max(1, Math.ceil(filteredCalls.length / PAGE_SIZE));
  const clampedPage = Math.min(page, pageCount - 1);
  const pagedCalls = filteredCalls.slice(clampedPage * PAGE_SIZE, clampedPage * PAGE_SIZE + PAGE_SIZE);

  if (notFound) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/endpoints" className="text-sm text-muted-foreground hover:underline">← Endpoints</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            No endpoint <code className="font-mono text-xs">{id}</code> has recorded any requests.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (detailQuery.isError) return <ErrorState message="Unable to load endpoint" />;
  if (detailQuery.isLoading || !detail) return <LoadingState />;

  const hasGroups = detail.groups.length > 0;
  const hasFilter = groupFilter !== null;

  return (
    <div>
      <div className="mb-4">
        <Link to="/endpoints" className="text-sm text-muted-foreground hover:underline">← Endpoints</Link>
        <div className="flex items-center gap-3 mt-1">
          <h1 className="text-2xl font-bold font-mono flex items-center gap-2">
            <span className="rounded bg-muted px-1.5 py-0.5 text-sm font-semibold uppercase text-muted-foreground">
              {detail.method}
            </span>
            <span>{detail.routeTemplate}</span>
          </h1>
          <HealthPill health={adapterHealth(detail)} />
        </div>
      </div>

      {/* Stat tiles */}
      <div className="grid grid-cols-3 gap-4 mb-4">
        <StatTile label="Total calls" value={detail.totalCalls.toLocaleString(DASHBOARD_LOCALE)} />
        <StatTile
          label="Error rate"
          value={formatPercent(detail.errorRate)}
          emphasis={detail.errorRate > 0 ? 'text-destructive' : undefined}
        />
        <StatTile label="Avg latency" value={formatMs(detail.avgDurationMs)} />
      </div>

      {/* Latency percentiles from the durable histogram — shown once there is request data */}
      <LatencyLine detail={detail} />

      {/* Performance over time — durable hourly volume + error + latency series */}
      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Performance over time</CardTitle></CardHeader>
        <CardContent>
          <PerformanceChart points={detail.history} />
        </CardContent>
      </Card>

      {/* Callers (groups) — only when calls carry a group; click a row to filter recent calls */}
      {hasGroups && (
        <Card className="mb-4">
          <CardHeader><CardTitle className="text-base">{detail.groupLabel}s</CardTitle></CardHeader>
          <CardContent className="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{detail.groupLabel}</TableHead>
                  <TableHead className="text-right w-24">Calls</TableHead>
                  <TableHead className="text-right w-24">Error %</TableHead>
                  <TableHead className="text-right w-28">Avg latency</TableHead>
                  <TableHead className="w-56">Last failure</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {detail.groups.map((g) => (
                  <Hint key={g.group} text="Filter recent calls by this caller">
                  <TableRow
                    className={`cursor-pointer ${groupFilter === g.group ? 'bg-accent' : ''}`}
                    onClick={() => setGroupFilter(groupFilter === g.group ? null : g.group)}
                  >
                    <TableCell className="font-mono text-sm">{g.group}</TableCell>
                    <TableCell className="text-right tabular-nums">{g.calls.toLocaleString(DASHBOARD_LOCALE)}</TableCell>
                    <TableCell className={`text-right tabular-nums ${g.errorRate > 0 ? 'text-destructive' : ''}`}>
                      {formatPercent(g.errorRate)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{formatMs(g.avgDurationMs)}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {g.lastFailureAt ? <RelativeTime date={g.lastFailureAt} /> : '—'}
                    </TableCell>
                  </TableRow>
                  </Hint>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}

      {/* Recent calls */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0">
          <CardTitle className="text-base">Recent calls</CardTitle>
          <div className="flex flex-wrap items-center gap-1.5">
            {groupFilter !== null && (
              <FilterChip label={`${detail.groupLabel}: ${groupFilter}`} onClear={() => setGroupFilter(null)} />
            )}
            {!hasFilter && hasGroups && (
              <span className="text-xs text-muted-foreground">Click a {detail.groupLabel.toLowerCase()} to filter</span>
            )}
          </div>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-56">Time</TableHead>
                <TableHead className="w-28">Outcome</TableHead>
                <TableHead className="text-right w-20">Status</TableHead>
                <TableHead className="text-right w-24">Duration</TableHead>
                {hasGroups && <TableHead>{detail.groupLabel}</TableHead>}
                <TableHead>Remote IP</TableHead>
                <TableHead>User</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {pagedCalls.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={hasGroups ? 7 : 6} className="text-center text-muted-foreground py-6">
                    {hasFilter ? 'No calls match this filter.' : 'No recent calls.'}
                  </TableCell>
                </TableRow>
              ) : (
                pagedCalls.map((call) => (
                  <TableRow
                    key={call.id}
                    className="cursor-pointer"
                    onClick={() => navigate(`/endpoints/${encodeURIComponent(id)}/calls/${encodeURIComponent(call.id)}`)}
                  >
                    <TableCell className="text-sm text-muted-foreground">
                      <RelativeTime date={call.timestamp} />
                    </TableCell>
                    <TableCell><OutcomeBadge outcome={call.outcome} /></TableCell>
                    <TableCell className="text-right tabular-nums text-sm"><HttpStatus code={call.statusCode} /></TableCell>
                    <TableCell className="text-right tabular-nums text-sm">{formatMs(call.durationMs)}</TableCell>
                    {hasGroups && (
                      <TableCell className="font-mono text-xs text-muted-foreground">{call.groupName ?? '—'}</TableCell>
                    )}
                    <TableCell className="font-mono text-xs text-muted-foreground">{call.remoteIp ?? '—'}</TableCell>
                    <TableCell className="text-sm text-muted-foreground truncate max-w-40">{call.user ?? '—'}</TableCell>
                  </TableRow>
                ))
              )}
            </TableBody>
          </Table>
        </CardContent>
        {filteredCalls.length > PAGE_SIZE && (
          <Pager
            page={clampedPage}
            pageCount={pageCount}
            total={filteredCalls.length}
            pageSize={PAGE_SIZE}
            onPrev={() => setPage((p) => Math.max(0, p - 1))}
            onNext={() => setPage((p) => Math.min(pageCount - 1, p + 1))}
          />
        )}
      </Card>
    </div>
  );
}

// Compact latency line under the stat tiles: avg + p90/p95/p99 from the durable histogram. Hidden when
// there is no data (all zero) so an endpoint with no traffic doesn't show a row of dashes.
function LatencyLine({
  detail,
}: {
  detail: { avgDurationMs: number; p90DurationMs: number; p95DurationMs: number; p99DurationMs: number };
}) {
  if (detail.avgDurationMs <= 0 && detail.p99DurationMs <= 0) {
    return null;
  }

  return (
    <div className="mb-4 -mt-1 text-sm text-muted-foreground">
      Latency: avg {formatMs(detail.avgDurationMs)} · p90 {formatMs(detail.p90DurationMs)} · p95{' '}
      {formatMs(detail.p95DurationMs)} · p99 {formatMs(detail.p99DurationMs)}
    </div>
  );
}

function StatTile({ label, value, emphasis }: { label: string; value: string; emphasis?: string }) {
  return (
    <Card>
      <CardContent className="p-4">
        <div className="text-sm text-muted-foreground">{label}</div>
        <div className={`text-2xl font-bold ${emphasis ?? ''}`}>{value}</div>
      </CardContent>
    </Card>
  );
}

function FilterChip({ label, onClear }: { label: string; onClear: () => void }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-primary/10 text-primary px-2 py-0.5 text-xs font-medium">
      {label}
      <Hint text="Clear filter">
        <button type="button" onClick={onClear} className="rounded-full hover:bg-primary/20 p-0.5" aria-label="Clear filter">
          <X className="h-3 w-3" />
        </button>
      </Hint>
    </span>
  );
}

function Pager({
  page,
  pageCount,
  total,
  pageSize,
  onPrev,
  onNext,
}: {
  page: number;
  pageCount: number;
  total: number;
  pageSize: number;
  onPrev: () => void;
  onNext: () => void;
}) {
  const from = page * pageSize + 1;
  const to = Math.min(total, (page + 1) * pageSize);

  return (
    <div className="flex items-center justify-between border-t px-4 py-2 text-sm text-muted-foreground">
      <span className="tabular-nums">{from}–{to} of {total}</span>
      <div className="flex items-center gap-1">
        <button
          type="button"
          onClick={onPrev}
          disabled={page === 0}
          className="inline-flex items-center gap-1 rounded-md px-2 py-1 hover:bg-accent disabled:opacity-40 disabled:hover:bg-transparent"
        >
          <ChevronLeft className="h-4 w-4" /> Prev
        </button>
        <span className="tabular-nums px-1">Page {page + 1} / {pageCount}</span>
        <button
          type="button"
          onClick={onNext}
          disabled={page >= pageCount - 1}
          className="inline-flex items-center gap-1 rounded-md px-2 py-1 hover:bg-accent disabled:opacity-40 disabled:hover:bg-transparent"
        >
          Next <ChevronRight className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}
