import { ExternalLink, Repeat, Trash2, X } from 'lucide-react';
import { Link } from 'react-router-dom';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { PageHeader, type PageHeaderMetaItem } from '@/components/v2/PageHeader';
import { shortId, shortType, stateName, formatDateTime } from '@/utils/format';
import { State } from '@/types';
import type { UnifiedJobDetailModel, JobLogModel } from '@/types';
import { useDeleteJob, useRequeueJob } from '@/api/hooks/useJobs';
import { JobTimeline } from './JobTimeline';
import { JobProgress } from './JobProgress';
import { JobLogs } from './JobLogs';
import { RelatedJobsSection } from './RelatedJobsSection';

function kindLabel(kind: number) {
  if (kind === 3) {
    return 'Batch';
  }
  if (kind === 2) {
    return 'Message';
  }

  return 'Job';
}

function pillClassForState(state: State): string {
  switch (state) {
    case State.Awaiting:    return 'awaiting';
    case State.Scheduled:   return 'scheduled';
    case State.Enqueued:    return 'enqueued';
    case State.Processing:  return 'processing';
    case State.Completed:   return 'completed';
    case State.Failed:      return 'failed';
    case State.Deleted:     return 'deleted';
    default:                return '';
  }
}

function relativeFromNow(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000)        return `${Math.max(1, Math.floor(diff / 1000))}s ago`;
  if (diff < 3_600_000)     return `${Math.floor(diff / 60_000)}m ago`;
  if (diff < 86_400_000)    return `${Math.floor(diff / 3_600_000)}h ago`;
  return `${Math.floor(diff / 86_400_000)}d ago`;
}

function formatJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

interface JobDetailStandardProps {
  job: UnifiedJobDetailModel;
  systemEvents: JobLogModel[];
  handlerLogs: JobLogModel[];
  reportedBars: Array<[string, number]>;
  jobCounts: Record<string, number>;
  onCountsUpdate: (counts: Record<string, number>) => void;
}

export function JobDetailStandard({
  job,
  systemEvents,
  handlerLogs,
  reportedBars,
  jobCounts,
  onCountsUpdate,
}: JobDetailStandardProps) {
  const requeue = useRequeueJob();
  const deleteJob = useDeleteJob();

  const isJob = job.kind === 1;
  const isProcessing = job.currentState === State.Processing;
  const hasChildJobs = job.kind === 2 || job.kind === 3;
  const kind = kindLabel(job.kind);

  const totalJobs =
    Object.keys(jobCounts).length > 0 ? Object.values(jobCounts).reduce((a, b) => a + b, 0) : job.totalJobs;
  const completedJobs = jobCounts['completed'] ?? job.completedJobs;
  const failedJobs = jobCounts['failed'] ?? job.failedJobs;

  const createdAgo = relativeFromNow(job.createTime);

  const meta: PageHeaderMetaItem[] = [];
  if (job.type) {
    meta.push({ k: 'Type', v: shortType(job.type) });
  }
  if (job.handlerType) {
    meta.push({ k: 'Handler', v: shortType(job.handlerType) });
  }
  if (job.queue) {
    meta.push({ k: 'Queue', v: job.queue });
  }
  meta.push({
    k: 'Created',
    v: formatDateTime(job.createTime),
    rel: createdAgo ?? undefined,
  });
  if (job.scheduleTime) {
    meta.push({ k: 'Scheduled', v: formatDateTime(job.scheduleTime) });
  }
  if (job.maxRetries > 0) {
    meta.push({ k: 'Attempts', v: `${job.retriedTimes + 1} / ${job.maxRetries + 1}` });
  }
  if (job.traceId) {
    meta.push({ k: 'Trace', v: job.traceId.slice(0, 12), copy: job.traceId });
  }
  meta.push({ k: 'ID', v: job.id, copy: job.id });

  const pillClass = pillClassForState(job.currentState);

  return (
    <div className="flex flex-col gap-[18px]">
      <PageHeader
        kindLabel={kind}
        title={shortId(job.id)}
        pill={<span className={`warp-pill ${pillClass}`}>{stateName(job.currentState)}</span>}
        actions={
          <>
            {isJob && (
              isProcessing ? (
                <button
                  type="button"
                  onClick={() => deleteJob.mutate(job.id)}
                  disabled={deleteJob.isPending}
                  className="inline-flex items-center gap-1.5 rounded-md border border-warp-red bg-warp-red-soft px-2.5 py-1.5 text-[12.5px] font-medium text-warp-red disabled:opacity-60"
                >
                  <X size={13} /> Cancel
                </button>
              ) : (
                <>
                  <button
                    type="button"
                    onClick={() => requeue.mutate(job.id)}
                    disabled={requeue.isPending}
                    className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-foreground hover:bg-panel-2 disabled:opacity-60"
                  >
                    <Repeat size={13} /> Requeue
                  </button>
                  <button
                    type="button"
                    onClick={() => deleteJob.mutate(job.id)}
                    disabled={deleteJob.isPending}
                    className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-warp-red hover:bg-warp-red-soft disabled:opacity-60"
                  >
                    <Trash2 size={13} /> Delete
                  </button>
                </>
              )
            )}
            {job.traceId && (
              <Link
                to={`/trace/${job.traceId}`}
                className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-foreground hover:bg-panel-2"
              >
                <ExternalLink size={13} /> Trace
              </Link>
            )}
          </>
        }
        meta={meta}
      />

      {/* BODY GRID */}
      <div className="grid grid-cols-1 gap-3.5 lg:grid-cols-2">
        <div className="flex flex-col gap-3.5">
          <JobProgress jobId={job.id} batch={{ totalJobs, completedJobs, failedJobs }} reportedBars={reportedBars} />
          {job.message && (
            <Panel>
              <PanelHeader eyebrow="Payload" />
              <pre className="mono m-0 max-h-[60vh] overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.7] text-text-dim">
                {formatJson(job.message)}
              </pre>
            </Panel>
          )}

          {job.metadata && Object.keys(job.metadata).length > 0 && (
            <Panel>
              <PanelHeader eyebrow="Metadata" />
              <pre className="mono m-0 max-h-60 overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.7] text-text-dim">
                {JSON.stringify(job.metadata, null, 2)}
              </pre>
            </Panel>
          )}
          {handlerLogs.length > 0 && <JobLogs jobId={job.id} logs={handlerLogs} />}
        </div>

        <div className="flex flex-col gap-3.5">
          {systemEvents.length > 0 && (
            <Panel>
              <PanelHeader eyebrow="Lifecycle" />
              <div className="px-4 py-3">
                <JobTimeline jobId={job.id} events={systemEvents} />
              </div>
            </Panel>
          )}
        </div>
      </div>

      {hasChildJobs && <RelatedJobsSection job={job} onCountsUpdate={onCountsUpdate} />}
    </div>
  );
}
