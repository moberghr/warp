import { useState, useEffect, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { StateBadge } from '@/components/StateBadge';
import { FlowCard } from '@/components/FlowCard';
import { FilteredJobsTable } from '@/components/FilteredJobsTable';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { RelativeTime } from '@/components/RelativeTime';
import { shortType, formatDateTime, shortId } from '@/utils/format';
import { LoadingState, ErrorState } from '@/components/PageState';
import { useRealtimeRefetch } from '@/hooks/useRealtimeRefetch';
import { State } from '@/types';
import type { UnifiedJobDetailModel, JobLogModel } from '@/types';
import * as api from '@/api';

type DetailPendingAction = 'cancel' | 'requeue' | 'delete' | 'cancelBatch';

// Color rules for the history panel: any event carrying an exception payload renders
// red (whole card, not just the inner <pre>), Completed renders green, everything else
// neutral. Do NOT add per-event-type entries to this map for non-error variants —
// Scheduled / Enqueued / Processing / Requeued / Created etc. should stay neutral so a
// timeline highlights only "this finished" / "this broke" rather than painting every
// row. Driving the red branch off `event.exception` instead of `eventType === "Failed"`
// catches edge cases like a Deleted row with a captured TimeoutException — anywhere a
// stack trace is attached, the card should look like an error at a glance.
const neutralRowClasses = {
  border: 'border-l-border',
  bg: 'bg-transparent',
  text: 'text-foreground',
};

const errorRowClasses = {
  border: 'border-l-red-500',
  bg: 'bg-red-50 dark:bg-red-950/30',
  text: 'text-red-700 dark:text-red-400',
};

// Failed is kept in the map as a fallback for the (uncommon) case where a pipeline
// behavior short-circuits state to Failed via Outcome.State = Failed without an
// attached exception — the row still represents a failure and should look like one.
const eventColors: Record<string, { border: string; bg: string; text: string }> = {
  Completed: { border: 'border-l-green-500', bg: 'bg-green-50 dark:bg-green-950/30', text: 'text-green-700 dark:text-green-400' },
  Failed:    errorRowClasses,
};

function formatDuration(ms: number): string {
  if (ms < 1) return '<1ms';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
  const mins = Math.floor(ms / 60000);
  const secs = ((ms % 60000) / 1000).toFixed(0);
  return `${mins}m ${secs}s`;
}

function getDuration(logs: JobLogModel[], currentIndex: number): string | null {
  const log = logs[currentIndex];
  if (log.durationMs != null) return formatDuration(log.durationMs);
  if (currentIndex >= logs.length - 1) return null;
  const current = new Date(logs[currentIndex].timestamp).getTime();
  const previous = new Date(logs[currentIndex + 1].timestamp).getTime();
  return formatDuration(current - previous);
}

function formatJson(raw: string): string {
  try { return JSON.stringify(JSON.parse(raw), null, 2); }
  catch { return raw; }
}

// Payload/metadata blocks default to a clamped height (160px) with overflow scroll so a
// huge JSON document doesn't push the rest of the detail page off-screen. An "Expand"
// toggle removes the clamp for users who want to read the whole thing inline.
function ExpandableJsonBlock({ heading, content }: { heading: string; content: string }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <h3 className="text-sm font-semibold">{heading}</h3>
        <button
          type="button"
          onClick={() => setExpanded((v) => !v)}
          className="text-xs text-muted-foreground hover:text-foreground"
        >
          {expanded ? 'Collapse' : 'Expand'}
        </button>
      </div>
      <pre
        className={`text-xs bg-muted p-3 rounded-md ${
          expanded ? 'whitespace-pre-wrap break-all' : 'overflow-auto max-h-40'
        }`}
      >
        {content}
      </pre>
    </div>
  );
}

function kindLabel(kind: number) {
  if (kind === 3) return 'Batch';
  if (kind === 2) return 'Message';
  return 'Job';
}

export default function DetailPage() {
  const { id } = useParams<{ id: string }>();
  const [job, setJob] = useState<UnifiedJobDetailModel | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [jobCounts, setJobCounts] = useState<Record<string, number>>({});
  const [pending, setPending] = useState<DetailPendingAction | null>(null);

  useEffect(() => {
    if (id) api.getDetail(id).then(setJob).catch(() => setError('Unable to load details'));
  }, [id]);

  const refresh = useCallback(() => {
    if (!id) return;
    api.getDetail(id).then(setJob).catch(() => {});
  }, [id]);

  // Refetch on every JobFinalized — the event is broadcast-only (no per-job scope in v1),
  // so we refetch even if the finalize is for an unrelated job. At normal dashboard usage
  // (<10 detail pages open) this is cheap; per-job groups are a v2 optimization.
  useRealtimeRefetch('JobFinalized', refresh);

  const handleCountsUpdate = useCallback((counts: Record<string, number>) => {
    setJobCounts(counts);
  }, []);

  if (error) return <ErrorState message={error} />;
  if (!job) return <LoadingState />;

  const systemEvents = job.logs.filter(l => l.eventType !== 'Log' && l.eventType !== 'Progress').reverse();
  const handlerLogs = job.logs.filter(l => l.eventType === 'Log');

  // Reported progress: latest value per bar name. Progress rows are append-only with
  // dedup-on-no-change, so the most recent entry per name is the current value.
  const progressByName = new Map<string, number>();
  for (const log of job.logs) {
    if (log.eventType !== 'Progress' || log.value == null) continue;
    const name = log.name ?? '';
    progressByName.set(name, log.value);
  }
  const progressBars = Array.from(progressByName.entries());

  // Batch progress
  const totalJobs = Object.keys(jobCounts).length > 0
    ? Object.values(jobCounts).reduce((a, b) => a + b, 0)
    : job.totalJobs;
  const completedJobs = jobCounts['completed'] ?? job.completedJobs;
  const failedJobs = jobCounts['failed'] ?? job.failedJobs;
  const done = completedJobs + failedJobs;
  const pct = totalJobs > 0 ? Math.round((done / totalJobs) * 100) : 0;
  const greenPct = totalJobs > 0 ? (completedJobs / totalJobs) * 100 : 0;
  const redPct = totalJobs > 0 ? (failedJobs / totalJobs) * 100 : 0;

  // Is this a container (batch/message) with child jobs?
  const hasChildJobs = job.kind === 2 || job.kind === 3;
  const isJob = job.kind === 1;

  const jobContext = JSON.stringify({ jobId: job.id });

  return (
    <div>
      {/* Header */}
      <div data-warp-slot="detail.header" data-warp-context={jobContext} key={`header-${job.id}`} className="flex items-center gap-4 mb-6">
        <h1 className="text-2xl font-bold">
          {job.type ? shortType(job.type) : kindLabel(job.kind)}{' '}
          <span className="font-mono text-base font-normal text-muted-foreground">{kindLabel(job.kind)} · {shortId(job.id)}</span>
        </h1>
        <StateBadge state={job.currentState} cancellationMode={job.cancellationMode} />
        {job.queue && <span className="text-sm text-muted-foreground">Queue: {job.queue}</span>}
        <div className="flex-1" />
        {isJob && job.currentState === State.Processing ? (
          <Button variant="destructive" size="sm" onClick={() => setPending('cancel')}>Cancel</Button>
        ) : isJob ? (
          <>
            <Button variant="outline" size="sm" onClick={() => setPending('requeue')}>Requeue</Button>
            <Button variant="destructive" size="sm" onClick={() => setPending('delete')}>Delete</Button>
          </>
        ) : job.kind === 3 && (job.currentState === State.Processing || job.currentState === State.Awaiting) ? (
          <Button variant="destructive" size="sm" onClick={() => setPending('cancelBatch')}>Cancel batch</Button>
        ) : null}
      </div>

      {/* Two-column layout */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Left column */}
        <div className="space-y-4">
          {/* Progress bar (batches) */}
          {totalJobs > 0 && (
            <div data-warp-slot="detail.progress" data-warp-context={jobContext} key={`progress-${job.id}`}>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm">Progress</CardTitle></CardHeader>
                <CardContent>
                  <div className="flex items-center gap-4">
                    <div className="flex-1 h-4 bg-muted rounded-full overflow-hidden flex">
                      {greenPct > 0 && <div className="h-full bg-green-500 transition-all" style={{ width: `${greenPct}%` }} />}
                      {redPct > 0 && <div className="h-full bg-red-500 transition-all" style={{ width: `${redPct}%` }} />}
                    </div>
                    <span className="text-sm font-medium">{done}/{totalJobs} ({pct}%)</span>
                  </div>
                </CardContent>
              </Card>
            </div>
          )}

          {/* Payload & Metadata */}
          {(job.message || (job.metadata && Object.keys(job.metadata).length > 0)) && (
            <div data-warp-slot="detail.payload" data-warp-context={jobContext} key={`payload-${job.id}`}>
              <Card>
                <CardContent className="pt-4 space-y-4">
                  {job.message && (
                    <ExpandableJsonBlock heading="Payload" content={formatJson(job.message)} />
                  )}
                  {job.metadata && Object.keys(job.metadata).length > 0 && (
                    <ExpandableJsonBlock heading="Metadata" content={JSON.stringify(job.metadata, null, 2)} />
                  )}
                </CardContent>
              </Card>
            </div>
          )}

          {/* Details */}
          <div data-warp-slot="detail.details" data-warp-context={jobContext} key={`details-${job.id}`}>
            <Card>
              <CardHeader className="pb-2"><CardTitle className="text-sm">Details</CardTitle></CardHeader>
              <CardContent className="space-y-2 text-sm">
                <div><span className="text-muted-foreground">Type:</span> {shortType(job.type)}</div>
                {job.handlerType && <div><span className="text-muted-foreground">Handler:</span> {shortType(job.handlerType)}</div>}
                <div><span className="text-muted-foreground">Created:</span> <RelativeTime date={job.createTime} /></div>
                {job.scheduleTime && <div><span className="text-muted-foreground">Scheduled:</span> <RelativeTime date={job.scheduleTime} /></div>}
                {job.metadata?.['ConcurrencyKey'] && <div><span className="text-muted-foreground">Mutex:</span> <span className="font-mono text-xs">{String(job.metadata['ConcurrencyKey'])}</span></div>}
                <div><span className="text-muted-foreground">ID:</span> <span className="font-mono text-xs">{job.id}</span></div>
              </CardContent>
            </Card>
          </div>

          {/* Origin: the inbound HTTP request that started this trace */}
          {job.origin && (
            <div data-warp-slot="detail.origin" data-warp-context={jobContext} key={`origin-${job.id}`}>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm">Origin</CardTitle></CardHeader>
                <CardContent className="space-y-2 text-sm">
                  <div>
                    <span className="text-muted-foreground">Request:</span>{' '}
                    <Link to={`/endpoints/${encodeURIComponent(job.origin.endpointId)}`} className="text-primary hover:underline font-mono text-xs">
                      {job.origin.method} {job.origin.routeTemplate}
                    </Link>
                  </div>
                  {job.origin.user && <div><span className="text-muted-foreground">User:</span> {job.origin.user}</div>}
                </CardContent>
              </Card>
            </div>
          )}

          {/* Flow */}
          <div data-warp-slot="detail.flow" data-warp-context={jobContext} key={`flow-${job.id}`}>
            <FlowCard
              jobId={job.id}
              traceId={job.traceId}
              parentJob={job.parentJob}
              spawnedByJob={job.spawnedByJob}
              continuations={job.continuations}
              spawnedJobs={job.spawnedJobs}
            />
          </div>
        </div>

        {/* Right column: Progress + History + Logs */}
        <div className="space-y-4">
          {/* Reported progress (handler-supplied via IJobContext.ReportProgress) */}
          {progressBars.length > 0 && (
            <div data-warp-slot="detail.reportedProgress" data-warp-context={jobContext} key={`reported-progress-${job.id}`}>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm">Reported Progress</CardTitle></CardHeader>
                <CardContent>
                  <div className="space-y-2">
                    {progressBars.map(([name, value]) => (
                      <div key={name}>
                        <div className="flex items-center justify-between text-xs mb-1">
                          <span className="text-muted-foreground">{name === '' ? 'Progress' : name}</span>
                          <span className="font-medium">{value}%</span>
                        </div>
                        <div className="h-2 bg-muted rounded-full overflow-hidden">
                          <div
                            className="h-full bg-blue-500 transition-all"
                            style={{ width: `${value}%` }}
                          />
                        </div>
                      </div>
                    ))}
                  </div>
                </CardContent>
              </Card>
            </div>
          )}

          {/* State History */}
          {systemEvents.length > 0 && (
            <div data-warp-slot="detail.history" data-warp-context={jobContext} key={`history-${job.id}`}>
              <h2 className="text-sm font-semibold text-muted-foreground uppercase mb-3">History</h2>
              <div className="space-y-3">
                {systemEvents.map((event, index) => {
                  const colors = event.exception
                    ? errorRowClasses
                    : (eventColors[event.eventType] ?? neutralRowClasses);
                  const duration = getDuration(systemEvents, index);
                  return (
                    <div key={event.id} className={`border-l-4 ${colors.border} ${colors.bg} rounded-r-md p-4`}>
                      <div className="flex items-center justify-between">
                        <span className={`font-semibold ${colors.text}`}>{event.eventType}</span>
                        <span className="text-xs text-muted-foreground">
                          <RelativeTime date={event.timestamp} />
                          {duration && <span className="ml-2 opacity-60">({duration})</span>}
                        </span>
                      </div>
                      {event.message && <p className="text-sm text-muted-foreground mt-1">{event.message}</p>}
                      {event.exception && (
                        <pre className="text-xs bg-red-100 dark:bg-red-950/50 text-red-800 dark:text-red-300 p-3 rounded-md overflow-auto mt-2 max-h-60">
                          {event.exception}
                        </pre>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Handler Logs */}
          {handlerLogs.length > 0 && (
            <div data-warp-slot="detail.logs" data-warp-context={jobContext} key={`logs-${job.id}`}>
              <Card>
                <CardHeader className="pb-2"><CardTitle className="text-sm">Handler Output ({handlerLogs.length})</CardTitle></CardHeader>
                <CardContent>
                  <div className="space-y-1 font-mono text-xs max-h-[80vh] overflow-auto">
                    {handlerLogs.map((log) => (
                      <div key={log.id} className={`flex gap-2 ${
                        log.level === 'Error' ? 'text-red-600' :
                        log.level === 'Warning' ? 'text-yellow-600' :
                        'text-muted-foreground'
                      }`}>
                        <span className="text-muted-foreground shrink-0">{formatDateTime(log.timestamp)}</span>
                        <span className="shrink-0 w-20">[{log.level}]</span>
                        <span className="break-all">{log.message}</span>
                      </div>
                    ))}
                  </div>
                </CardContent>
              </Card>
            </div>
          )}
        </div>
      </div>

      {/* Child jobs table (batches and messages) */}
      {hasChildJobs && (
        <div className="mt-6">
          <FilteredJobsTable
            key={job.id}
            title="Jobs"
            fetchJobs={(page, pageSize, state) =>
              job.kind === 3
                ? api.getBatchJobs(job.id, page, pageSize, state)
                : api.getMessageJobs(job.id, page, pageSize, state)
            }
            fetchCounts={() =>
              job.kind === 3
                ? api.getBatchJobCounts(job.id)
                : api.getMessageJobCounts(job.id)
            }
            onCountsUpdate={handleCountsUpdate}
          />
        </div>
      )}

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={
          pending === 'cancel' ? 'Cancel running job?'
            : pending === 'requeue' ? 'Requeue job?'
              : pending === 'delete' ? 'Delete job?'
                : pending === 'cancelBatch' ? 'Cancel batch?'
                  : ''
        }
        description={
          pending === 'cancel'
            ? 'The job will be marked for graceful cancellation. If the handler ignores the cancellation token and completes, the job stays in its current state.'
            : pending === 'requeue'
              ? 'The job will be re-enqueued and picked up by a worker on the next poll.'
              : pending === 'delete'
                ? 'The job will be removed permanently. This cannot be undone.'
                : pending === 'cancelBatch'
                  ? 'Every child job that has not finished yet will be cancelled: enqueued/scheduled children are deleted, and running children are signalled for graceful cancellation. Completed and failed children are left as-is.'
                  : null
        }
        confirmLabel={pending === 'requeue' ? 'Requeue' : pending === 'cancel' ? 'Cancel job' : pending === 'cancelBatch' ? 'Cancel batch' : 'Delete'}
        variant={pending === 'delete' || pending === 'cancel' || pending === 'cancelBatch' ? 'destructive' : 'default'}
        onConfirm={() => {
          if (pending === 'cancel' || pending === 'delete') api.deleteJob(job.id);
          else if (pending === 'requeue') api.requeueJob(job.id);
          else if (pending === 'cancelBatch') api.cancelBatch(job.id);
          setPending(null);
        }}
      />
    </div>
  );
}
