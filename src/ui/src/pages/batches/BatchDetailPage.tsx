import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { Copy, RotateCw, Trash2 } from 'lucide-react';
import { ProgressCard } from '@/components/v2/ProgressCard';
import { LifecycleCard, type LifecycleEvent } from '@/components/v2/LifecycleCard';
import { BatchJobsTable } from './BatchJobsTable';
import { shortType, stateName, formatDateTime } from '@/utils/format';
import { State } from '@/types';
import type { UnifiedJobDetailModel, JobLogModel } from '@/types';
import { useDeleteJob, useRequeueJob } from '@/api/hooks/useJobs';
import { useConfirm } from '@/components/forms/useConfirm';

function relativeFromNow(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) {
    return `${Math.max(1, Math.floor(diff / 1000))}s ago`;
  }
  if (diff < 3_600_000) {
    return `${Math.floor(diff / 60_000)}m ago`;
  }
  if (diff < 86_400_000) {
    return `${Math.floor(diff / 3_600_000)}h ago`;
  }

  return `${Math.floor(diff / 86_400_000)}d ago`;
}

const eventKindMap: Record<string, LifecycleEvent['kind']> = {
  Created: 'created',
  Enqueued: 'enqueued',
  Scheduled: 'scheduled',
  Processing: 'processing',
  Completed: 'completed',
  Failed: 'failed',
  Retried: 'progress',
  Requeued: 'progress',
  Deleted: 'deleted',
};

function logsToEvents(logs: JobLogModel[]): LifecycleEvent[] {
  return logs.map(l => {
    const kind = eventKindMap[l.eventType] ?? 'created';
    const ts = new Date(l.timestamp);
    const rel = relativeFromNow(l.timestamp);

    return {
      kind,
      label: l.eventType,
      when: `${formatDateTime(ts)}${rel ? ` · ${rel}` : ''}`,
      message: l.message ?? undefined,
    };
  });
}

interface BatchDetailPageProps {
  job: UnifiedJobDetailModel;
  systemEvents: JobLogModel[];
}

export function BatchDetailPage({ job, systemEvents }: BatchDetailPageProps) {
  const requeue = useRequeueJob();
  const deleteJob = useDeleteJob();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [jobCounts, setJobCounts] = useState<Record<string, number>>({});

  const askRequeue = async () => {
    const ok = await confirm({
      title: 'Requeue batch?',
      description: `Requeue batch ${job.id.slice(0, 8)}? It will be re-executed immediately.`,
      confirmLabel: 'Requeue',
    });
    if (ok) {
      requeue.mutate(job.id);
    }
  };

  const askDelete = async () => {
    const ok = await confirm({
      title: 'Delete batch?',
      description: `Delete batch ${job.id.slice(0, 8)} and stop activating its children? This cannot be undone.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (ok) {
      deleteJob.mutate(job.id);
    }
  };

  const handleCountsUpdate = useCallback((counts: Record<string, number>) => {
    setJobCounts(counts);
  }, []);

  const total =
    Object.keys(jobCounts).length > 0 ? Object.values(jobCounts).reduce((a, b) => a + b, 0) : job.totalJobs;
  const completed = jobCounts['completed'] ?? job.completedJobs;
  const failed = jobCounts['failed'] ?? job.failedJobs;
  const processing = jobCounts['processing'] ?? 0;
  const awaiting = Math.max(0, total - completed - failed - processing);

  const createdAgo = relativeFromNow(job.createTime);
  const events = logsToEvents(systemEvents);

  const pillClass = pillClassForState(job.currentState);
  const stateLabel = stateName(job.currentState);

  const metaItems: Array<[string, string, boolean]> = [];
  metaItems.push([
    'Created',
    `${formatDateTime(job.createTime)}${createdAgo ? ` · ${createdAgo}` : ''}`,
    false,
  ]);
  if (job.queue) metaItems.push(['Queue', job.queue, false]);
  if (job.scheduleTime) metaItems.push(['Scheduled', formatDateTime(job.scheduleTime), false]);
  metaItems.push(['ID', job.id, true]);
  if (job.traceId) metaItems.push(['Trace', job.traceId, true]);

  return (
    <div className="flex flex-col gap-[18px]">
      <div style={{ padding: '20px 0 18px', borderBottom: '1px solid var(--hair)' }}>
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <div
            className="mono"
            style={{
              fontSize: 11.5,
              color: 'var(--text-mute)',
              letterSpacing: 0.4,
            }}
          >
            <Link to="/batches" className="hover:text-foreground">
              Batches
            </Link>
            <span style={{ margin: '0 7px', opacity: 0.5 }}>/</span>
            <span>{stateLabel}</span>
            <span style={{ margin: '0 7px', opacity: 0.5 }}>/</span>
            <span style={{ color: 'var(--foreground)' }}>{job.id.slice(0, 8)}</span>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={askRequeue}
              disabled={requeue.isPending}
              className="soft-btn soft-btn-ghost"
            >
              <RotateCw size={14} /> Requeue
            </button>
            <button
              type="button"
              onClick={askDelete}
              disabled={deleteJob.isPending}
              className="soft-btn soft-btn-danger"
            >
              <Trash2 size={14} /> Delete
            </button>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-3.5 flex-wrap">
          <span
            className="font-semibold text-foreground"
            style={{ fontSize: 32, letterSpacing: '-0.6px', lineHeight: 1 }}
          >
            Batch
          </span>
          <span
            className="mono font-medium text-foreground tabular-nums"
            style={{ fontSize: 32, letterSpacing: '-0.6px', lineHeight: 1 }}
          >
            {job.id.slice(0, 8)}
          </span>
          <span className={`soft-pill ${pillClass}`}>{stateLabel}</span>
          {job.type && (
            <span className="inline-flex items-baseline gap-1.5" style={{ fontSize: 14 }}>
              <span className="soft-eyebrow">Request</span>
              <span className="text-text-dim font-medium">{shortType(job.type)}</span>
            </span>
          )}
          {job.handlerType && (
            <span className="inline-flex items-baseline gap-1.5" style={{ fontSize: 14 }}>
              <span className="soft-eyebrow">Handler</span>
              <span className="text-text-dim font-medium">{shortType(job.handlerType)}</span>
            </span>
          )}
        </div>

        <div className="mt-4 flex flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-7 sm:gap-y-2 min-w-0">
          {metaItems.map(([k, v, copy]) => (
            <div
              key={k}
              className="flex items-baseline gap-2.5 min-w-0"
            >
              <span className="soft-eyebrow shrink-0">{k}</span>
              <span
                className="mono text-foreground break-all"
                style={{ fontSize: 12.5, letterSpacing: 0.2 }}
              >
                {v}
              </span>
              {copy && (
                <button
                  type="button"
                  onClick={() => void navigator.clipboard?.writeText(v)}
                  className="text-text-mute hover:text-foreground shrink-0"
                  aria-label={`Copy ${k}`}
                >
                  <Copy size={12} />
                </button>
              )}
            </div>
          ))}
        </div>
      </div>

      <div className="warp-info-row">
        <ProgressCard
          total={total}
          breakdown={{
            awaiting,
            processing,
            completed,
            failed,
          }}
        />
        <LifecycleCard events={events} />
      </div>

      <section className="flex flex-col gap-[14px]">
        <div className="warp-section-head">
          <div className="warp-section-title">
            <h2>Jobs</h2>
            <span className="ct">({total})</span>
          </div>
        </div>
        <BatchJobsTable key={job.id} parentId={job.id} onCountsUpdate={handleCountsUpdate} />
      </section>
      {confirmDialog}
    </div>
  );
}

function pillClassForState(state: State): string {
  switch (state) {
    case State.Awaiting:
      return 'awaiting';
    case State.Scheduled:
      return 'scheduled';
    case State.Enqueued:
      return 'enqueued';
    case State.Processing:
      return 'processing';
    case State.Completed:
      return 'completed';
    case State.Failed:
      return 'failed';
    case State.Deleted:
      return 'deleted';
    default:
      return '';
  }
}
