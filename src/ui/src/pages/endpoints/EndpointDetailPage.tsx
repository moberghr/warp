import { useEffect, useMemo, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { X, ChevronLeft, ChevronRight } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { StateBadge } from '@/components/StateBadge';
import type { State } from '@/types';
import type { EndpointRelatedJob } from '@/types/endpoints';
import * as api from '@/api';
import { HealthPill, adapterHealth, OutcomeBadge, formatPercent, formatMs } from '../adapters/shared';

const PAGE_SIZE = 15;

export default function EndpointDetailPage() {
  const { id: rawId } = useParams<{ id: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';

  // Filter for the recent-calls list — driven by clicking a caller (group) row.
  const [groupFilter, setGroupFilter] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  const [selectedCallId, setSelectedCallId] = useState<string | null>(null);

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
        <StatTile label="Total calls" value={detail.totalCalls.toLocaleString()} />
        <StatTile
          label="Error rate"
          value={formatPercent(detail.errorRate)}
          emphasis={detail.errorRate > 0 ? 'text-destructive' : undefined}
        />
        <StatTile label="Avg latency" value={formatMs(detail.avgDurationMs)} />
      </div>

      {/* Latency percentiles from the durable histogram — shown once there is request data */}
      <LatencyLine detail={detail} />

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
                  <TableRow
                    key={g.group}
                    className={`cursor-pointer ${groupFilter === g.group ? 'bg-accent' : ''}`}
                    onClick={() => setGroupFilter(groupFilter === g.group ? null : g.group)}
                    title="Filter recent calls by this caller"
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
                    onClick={() => setSelectedCallId(call.id)}
                  >
                    <TableCell className="text-sm text-muted-foreground">
                      <RelativeTime date={call.timestamp} />
                    </TableCell>
                    <TableCell><OutcomeBadge outcome={call.outcome} /></TableCell>
                    <TableCell className="text-right tabular-nums text-sm">{call.statusCode ?? '—'}</TableCell>
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

      {selectedCallId && (
        <CallDrawer id={id} callId={selectedCallId} onClose={() => setSelectedCallId(null)} />
      )}
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

function CallDrawer({ id, callId, onClose }: { id: string; callId: string; onClose: () => void }) {
  const query = useQuery({
    queryKey: ['endpoints', 'call', id, callId] as const,
    queryFn: () => api.getEndpointCall(id, callId),
  });

  const call = query.data;

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/40" onClick={onClose}>
      <div
        className="h-full w-full max-w-2xl overflow-y-auto bg-card p-6 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start justify-between mb-4">
          <h2 className="text-lg font-semibold">Call detail</h2>
          <button
            type="button"
            onClick={onClose}
            className="p-1 rounded-md hover:bg-accent text-muted-foreground"
            title="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {query.isError && <ErrorState message="Unable to load call detail" />}
        {query.isLoading && <LoadingState />}

        {call && (
          <div className="space-y-4">
            <div className="flex items-center gap-2">
              <span className="font-mono text-sm">
                <span className="mr-1 font-semibold uppercase text-muted-foreground">{call.method}</span>
                {call.routeTemplate}
              </span>
              <OutcomeBadge outcome={call.outcome} />
            </div>

            <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
              <Field label="Timestamp"><RelativeTime date={call.timestamp} /></Field>
              <Field label="Duration">{formatMs(call.durationMs)}</Field>
              <Field label="Status">{call.statusCode ?? '—'}</Field>
              {call.groupName && <Field label="Caller"><span className="font-mono text-xs">{call.groupName}</span></Field>}
              <Field label="Remote IP"><span className="font-mono text-xs">{call.remoteIp ?? '—'}</span></Field>
              <Field label="User"><span className="font-mono text-xs">{call.user ?? '—'}</span></Field>
              <Field label="Machine"><span className="font-mono text-xs">{call.machineName}</span></Field>
              {call.traceId && <Field label="Trace"><span className="font-mono text-xs">{call.traceId}</span></Field>}
            </div>

            {call.userAgent && (
              <Pane title="User agent">
                <div className="font-mono text-xs break-words">{call.userAgent}</div>
              </Pane>
            )}

            {call.exceptionType && (
              <Pane title="Exception">
                <div className="font-mono text-xs text-destructive">{call.exceptionType}</div>
                {call.exceptionMessage && (
                  <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">{call.exceptionMessage}</pre>
                )}
              </Pane>
            )}

            <PayloadPane
              title="Request"
              headers={call.requestHeaders}
              body={call.requestBody}
            />
            <PayloadPane
              title="Response"
              headers={call.responseHeaders}
              body={call.responseBody}
            />

            <TagsSection tagsJson={call.tagsJson} />

            <RelatedJobsSection jobs={call.relatedJobs} traceId={call.traceId} />
          </div>
        )}
      </div>
    </div>
  );
}

function PayloadPane({
  title,
  headers,
  body,
}: {
  title: string;
  headers: string | null;
  body: string | null;
}) {
  const hasAny = !!headers || !!body;

  return (
    <Pane title={title}>
      {!hasAny && <div className="text-xs text-muted-foreground">Not captured.</div>}
      {headers && (
        <div className="mb-2">
          <div className="text-xs text-muted-foreground mb-0.5">Headers</div>
          <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/50 p-2 font-mono text-xs">{headers}</pre>
        </div>
      )}
      {body && (
        <div>
          <div className="text-xs text-muted-foreground mb-0.5">Body</div>
          <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/50 p-2 font-mono text-xs max-h-72 overflow-auto">{body}</pre>
        </div>
      )}
    </Pane>
  );
}

// Custom enrichment tags come from the recorder as a JSON object of string→string. Render defensively —
// a null/empty or malformed payload skips the whole section rather than crashing.
function TagsSection({ tagsJson }: { tagsJson: string | null }) {
  const tags = useMemo(() => parseTags(tagsJson), [tagsJson]);
  if (tags.length === 0) {
    return null;
  }

  return (
    <Pane title="Tags">
      <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
        {tags.map(([key, value]) => (
          <div key={key} className="flex gap-2">
            <span className="text-muted-foreground min-w-24 font-mono text-xs">{key}</span>
            <span className="font-mono text-xs break-words">{value}</span>
          </div>
        ))}
      </div>
    </Pane>
  );
}

// Jobs enqueued during this request (same trace id) — the request→jobs drill-down. Skipped entirely when
// no jobs were spawned; a "View full trace" link is shown when the request carried a trace id.
function RelatedJobsSection({ jobs, traceId }: { jobs: EndpointRelatedJob[]; traceId: string | null }) {
  if (jobs.length === 0) {
    return null;
  }

  return (
    <Pane title="Related jobs">
      <div className="space-y-1.5">
        {jobs.map((job) => (
          <div key={job.id} className="flex items-center gap-2 text-sm">
            <Link to={`/detail/${job.id}`} className="font-mono text-xs text-primary hover:underline truncate max-w-56">
              {job.type ?? job.id}
            </Link>
            <StateBadge state={job.state as State} />
            <span className="font-mono text-xs text-muted-foreground">{job.queue}</span>
          </div>
        ))}
      </div>
      {traceId && (
        <div className="mt-2">
          <Link to={`/trace/${traceId}`} className="text-xs text-primary hover:underline">
            View full trace →
          </Link>
        </div>
      )}
    </Pane>
  );
}

// Redacted, truncated tags as a JSON object of string→string. A malformed or non-object payload yields
// no pairs rather than a crash.
function parseTags(tagsJson: string | null): [string, string][] {
  if (!tagsJson) {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(tagsJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return [];
    }

    return Object.entries(parsed as Record<string, unknown>).map(([key, value]) => [key, String(value)]);
  } catch {
    return [];
  }
}

function Pane({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-md border p-3">
      <div className="text-sm font-medium mb-2">{title}</div>
      {children}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-2">
      <span className="text-muted-foreground min-w-24">{label}</span>
      <span>{children}</span>
    </div>
  );
}
