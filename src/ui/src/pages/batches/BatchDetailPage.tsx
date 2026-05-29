import { useCallback, useState } from 'react';
import { Repeat, Trash2 } from 'lucide-react';
import { PageHeader, type PageHeaderMetaItem } from '@/components/v2/PageHeader';
import { ProgressCard } from '@/components/v2/ProgressCard';
import { LifecycleCard, type LifecycleEvent } from '@/components/v2/LifecycleCard';
import { BatchJobsTable } from './BatchJobsTable';
import { shortId, shortType, stateName, formatDateTime } from '@/utils/format';
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

  const askDelete = async () => {
    const ok = await confirm({
      title: 'Delete batch?',
      description: `Delete batch ${shortId(job.id)} and stop activating its children? This cannot be undone.`,
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

  const meta: PageHeaderMetaItem[] = [];
  if (job.type) {
    meta.push({ k: 'Type', v: shortType(job.type) });
  }
  if (job.queue) {
    meta.push({ k: 'Queue', v: job.queue });
  }
  meta.push({
    k: 'Created',
    v: formatDateTime(job.createTime),
    rel: createdAgo ?? undefined,
  });
  meta.push({ k: 'ID', v: job.id, copy: job.id });

  const pillClass = pillClassForState(job.currentState);

  return (
    <div className="flex flex-col gap-[18px]">
      <PageHeader
        kindLabel="Batch"
        title={shortId(job.id)}
        pill={<span className={`warp-pill ${pillClass}`}>{stateName(job.currentState)}</span>}
        actions={
          <>
            <button
              type="button"
              onClick={() => requeue.mutate(job.id)}
              disabled={requeue.isPending}
              className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-foreground hover:bg-panel-2 disabled:opacity-60"
            >
              <Repeat size={13} /> Retry
            </button>
            <button
              type="button"
              onClick={askDelete}
              disabled={deleteJob.isPending}
              className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-warp-red hover:bg-warp-red-soft disabled:opacity-60"
            >
              <Trash2 size={13} /> Delete
            </button>
          </>
        }
        meta={meta}
      />

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
