import { useEffect, useMemo, useState } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { AlertTriangle, X, ChevronLeft, ChevronRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { PerformanceChart } from '@/components/PerformanceChart';
import * as api from '@/api';
import { HealthPill, adapterHealth, OutcomeBadge, HttpStatus, formatPercent, formatMs, parseTags } from './shared';

const PAGE_SIZE = 15;

export default function AdapterDetailPage() {
  const { name: rawName } = useParams<{ name: string }>();
  const name = rawName ? decodeURIComponent(rawName) : '';
  const navigate = useNavigate();

  // Combined filter for the recent-calls list — driven by clicking an operation row or a group row.
  const [operationFilter, setOperationFilter] = useState<string | null>(null);
  const [groupFilter, setGroupFilter] = useState<string | null>(null);
  const [page, setPage] = useState(0);

  const detailQuery = useQuery({
    queryKey: ['adapters', 'detail', name] as const,
    queryFn: () => api.getAdapterDetail(name),
    enabled: !!name,
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

    return detail.recentCalls.filter(
      (x) =>
        (operationFilter === null || x.operation === operationFilter) &&
        (groupFilter === null || x.groupName === groupFilter),
    );
  }, [detail, operationFilter, groupFilter]);

  // Reset to the first page whenever the filter changes so the view never lands on an empty page.
  useEffect(() => {
    setPage(0);
  }, [operationFilter, groupFilter]);

  const pageCount = Math.max(1, Math.ceil(filteredCalls.length / PAGE_SIZE));
  const clampedPage = Math.min(page, pageCount - 1);
  const pagedCalls = filteredCalls.slice(clampedPage * PAGE_SIZE, clampedPage * PAGE_SIZE + PAGE_SIZE);

  if (notFound) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/adapters" className="text-sm text-muted-foreground hover:underline">← Adapters</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            No adapter named <code className="font-mono text-xs">{name}</code> has recorded any calls.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (detailQuery.isError) return <ErrorState message="Unable to load adapter" />;
  if (detailQuery.isLoading || !detail) return <LoadingState />;

  const hasGroups = detail.groups.length > 0;
  const hasFilter = operationFilter !== null || groupFilter !== null;

  return (
    <div>
      <div className="mb-4">
        <Link to="/adapters" className="text-sm text-muted-foreground hover:underline">← Adapters</Link>
        <div className="flex items-center gap-3 mt-1">
          <h1 className="text-2xl font-bold">{detail.name}</h1>
          <HealthPill health={adapterHealth(detail)} />
          {detail.hasPolicyConflict && (
            <span
              className="inline-flex items-center gap-1 rounded-full bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300 px-2 py-0.5 text-xs font-medium"
              title="This process reported a shared rate-limit policy that differs from the persisted definition; the persisted policy is being enforced."
            >
              <AlertTriangle className="h-3 w-3" />
              Policy conflict
            </span>
          )}
        </div>
      </div>

      {/* Stat tiles */}
      <div className="grid grid-cols-3 gap-4 mb-4">
        <StatTile label="Total calls" value={detail.totalCalls.toLocaleString()} />
        <StatTile
          label="Error rate"
          value={formatPercent(detail.errorRate)}
          emphasis={detail.errorRate > 0 ? 'text-destructive' : undefined}
        />
        <StatTile label="Avg latency" value={formatMs(detail.avgDurationMs)} />
      </div>

      {/* Latency percentiles from the durable histogram — shown once there is call data */}
      <LatencyLine detail={detail} />

      {/* Policy — parsed from the config summary into labeled badges */}
      <PolicyCard configSummary={detail.configSummary} hasConflict={detail.hasPolicyConflict} firstSeenAt={detail.firstSeenAt} />

      {/* Performance over time — durable hourly volume + error + latency series */}
      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Performance over time</CardTitle></CardHeader>
        <CardContent>
          <PerformanceChart points={detail.history} />
        </CardContent>
      </Card>

      {/* Operations — click a row to filter the recent-calls list to that operation */}
      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Operations</CardTitle></CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Operation</TableHead>
                <TableHead className="text-right w-24">Calls</TableHead>
                <TableHead className="text-right w-24">Errors</TableHead>
                <TableHead className="text-right w-24">Error %</TableHead>
                <TableHead className="text-right w-28">Avg latency</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {detail.operations.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={5} className="text-center text-muted-foreground py-6">
                    No operations recorded yet.
                  </TableCell>
                </TableRow>
              ) : (
                detail.operations.map((op) => (
                  <TableRow
                    key={op.operation}
                    className={`cursor-pointer ${operationFilter === op.operation ? 'bg-accent' : ''}`}
                    onClick={() => setOperationFilter(operationFilter === op.operation ? null : op.operation)}
                    title="Filter recent calls by this operation"
                  >
                    <TableCell className="font-mono text-sm">{op.operation}</TableCell>
                    <TableCell className="text-right tabular-nums">{op.calls.toLocaleString()}</TableCell>
                    <TableCell className="text-right tabular-nums">{op.errors.toLocaleString()}</TableCell>
                    <TableCell className={`text-right tabular-nums ${op.errorRate > 0 ? 'text-destructive' : ''}`}>
                      {formatPercent(op.errorRate)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{formatMs(op.avgDurationMs)}</TableCell>
                  </TableRow>
                ))
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {/* Groups — only when the adapter carries groups; click a row to filter recent calls by that group */}
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
                  <TableRow
                    key={g.group}
                    className={`cursor-pointer ${groupFilter === g.group ? 'bg-accent' : ''}`}
                    onClick={() => setGroupFilter(groupFilter === g.group ? null : g.group)}
                    title="Filter recent calls by this group"
                  >
                    <TableCell className="font-mono text-sm">{g.group}</TableCell>
                    <TableCell className="text-right tabular-nums">{g.calls.toLocaleString()}</TableCell>
                    <TableCell className={`text-right tabular-nums ${g.errorRate > 0 ? 'text-destructive' : ''}`}>
                      {formatPercent(g.errorRate)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{formatMs(g.avgDurationMs)}</TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {g.lastFailureAt ? <RelativeTime date={g.lastFailureAt} /> : '—'}
                    </TableCell>
                  </TableRow>
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
            {operationFilter !== null && (
              <FilterChip label={`Operation: ${operationFilter}`} onClear={() => setOperationFilter(null)} />
            )}
            {groupFilter !== null && (
              <FilterChip label={`${detail.groupLabel}: ${groupFilter}`} onClear={() => setGroupFilter(null)} />
            )}
            {!hasFilter && (
              <span className="text-xs text-muted-foreground">Click an operation or {detail.groupLabel.toLowerCase()} to filter</span>
            )}
          </div>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-56">Time</TableHead>
                <TableHead>Operation</TableHead>
                {hasGroups && <TableHead>{detail.groupLabel}</TableHead>}
                <TableHead className="w-28">Outcome</TableHead>
                <TableHead className="text-right w-20">Status</TableHead>
                <TableHead className="text-right w-24">Duration</TableHead>
                <TableHead>Tags</TableHead>
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
                    onClick={() => navigate(`/adapters/${encodeURIComponent(name)}/calls/${encodeURIComponent(call.id)}`)}
                  >
                    <TableCell className="text-sm text-muted-foreground">
                      <RelativeTime date={call.timestamp} />
                    </TableCell>
                    <TableCell className="font-mono text-sm">{call.operation}</TableCell>
                    {hasGroups && (
                      <TableCell className="font-mono text-xs text-muted-foreground">{call.groupName ?? '—'}</TableCell>
                    )}
                    <TableCell><OutcomeBadge outcome={call.outcome} /></TableCell>
                    <TableCell className="text-right tabular-nums text-sm"><HttpStatus code={call.statusCode} /></TableCell>
                    <TableCell className="text-right tabular-nums text-sm">{formatMs(call.durationMs)}</TableCell>
                    <TableCell><Tags tagsJson={call.tagsJson} /></TableCell>
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
// there is no data (all zero) so a freshly-registered adapter doesn't show a row of dashes.
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

// The persisted config summary has the shape:
//   record=All; capture req-body=OnFailure, resp-body=OnFailure, headers=None; resilience=on; shared-limit=50/10s (Wait)
// Parse it into structured pieces for a tidy badge layout, falling back to the raw string if the shape drifts.
function parsePolicy(summary: string | null) {
  if (!summary) {
    return null;
  }

  const pick = (re: RegExp) => summary.match(re)?.[1]?.trim();
  const record = pick(/record=([^;]+)/);
  const reqBody = pick(/req-body=([^,;]+)/);
  const respBody = pick(/resp-body=([^,;]+)/);
  const headers = pick(/headers=([^,;]+)/);
  const resilience = pick(/resilience=([^;]+)/);
  const sharedLimit = pick(/shared-limit=([^;]+)/);

  if (!record || !resilience) {
    return null;
  }

  return { record, reqBody, respBody, headers, resilience, sharedLimit };
}

function PolicyCard({
  configSummary,
  hasConflict,
  firstSeenAt,
}: {
  configSummary: string | null;
  hasConflict: boolean;
  firstSeenAt: string;
}) {
  const policy = parsePolicy(configSummary);

  return (
    <Card className="mb-4">
      <CardContent className="p-4">
        <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
          <span className="text-sm font-medium text-muted-foreground">Policy</span>

          {policy === null && configSummary === null && (
            <span className="text-sm text-muted-foreground">Observe-only — no policies configured.</span>
          )}
          {policy === null && configSummary !== null && (
            <span className="font-mono text-xs break-words">{configSummary}</span>
          )}

          {policy !== null && (
            <div className="flex flex-wrap items-center gap-1.5">
              <PolicyBadge label="Records" value={policy.record === 'All' ? 'all calls' : 'failures only'} tone="neutral" />
              <CaptureBadge label="req body" value={policy.reqBody} />
              <CaptureBadge label="resp body" value={policy.respBody} />
              <CaptureBadge label="headers" value={policy.headers} />
              <PolicyBadge
                label="Resilience"
                value={policy.resilience}
                tone={policy.resilience === 'on' ? 'good' : 'off'}
              />
              <PolicyBadge
                label="Rate limit"
                value={policy.sharedLimit ?? 'none'}
                tone={policy.sharedLimit && policy.sharedLimit !== 'none' ? 'info' : 'off'}
              />
            </div>
          )}

          <div className="ml-auto text-xs text-muted-foreground">
            {hasConflict ? (
              <span className="text-amber-600 dark:text-amber-400">Enforcing persisted shared policy</span>
            ) : (
              <>First seen <RelativeTime date={firstSeenAt} /></>
            )}
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

type BadgeTone = 'neutral' | 'good' | 'off' | 'info' | 'warn';

const toneClasses: Record<BadgeTone, string> = {
  neutral: 'bg-muted text-foreground',
  good: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300',
  info: 'bg-sky-100 text-sky-800 dark:bg-sky-900/40 dark:text-sky-300',
  warn: 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300',
  off: 'bg-muted text-muted-foreground',
};

function PolicyBadge({ label, value, tone }: { label: string; value: string; tone: BadgeTone }) {
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${toneClasses[tone]}`}>
      <span className="opacity-70">{label}</span>
      <span>{value}</span>
    </span>
  );
}

// Capture tiers: None reads as muted/off, OnFailure as warn, Always as good — so at a glance you can see
// what payload the adapter is persisting.
function CaptureBadge({ label, value }: { label: string; value: string | undefined }) {
  const tone: BadgeTone = value === 'Always' ? 'good' : value === 'OnFailure' ? 'warn' : 'off';

  return <PolicyBadge label={label} value={value ?? 'None'} tone={tone} />;
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

function Tags({ tagsJson }: { tagsJson: string | null }) {
  const tags = useMemo(() => parseTags(tagsJson), [tagsJson]);
  if (tags.length === 0) {
    return <span className="text-muted-foreground/40">—</span>;
  }

  return (
    <div className="flex flex-wrap gap-1">
      {tags.map(([key, value]) => (
        <span key={key} className="rounded bg-muted px-1.5 py-0.5 text-[11px] font-mono text-muted-foreground">
          {key}={value}
        </span>
      ))}
    </div>
  );
}
