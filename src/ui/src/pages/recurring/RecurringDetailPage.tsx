import { useState, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { StateBadge } from '@/components/StateBadge';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePageStore } from '@/stores/page';
import { shortType, formatDateTime, shortId } from '@/utils/format';
import {
  useRecurringDetail,
  useRecurringJobs,
  useEnableRecurringJob,
  useDisableRecurringJob,
  useTriggerRecurringJob,
  useDeleteRecurringJob,
} from '@/api/hooks/useRecurring';
import { useConfirm } from '@/components/forms/useConfirm';
import cronstrue from 'cronstrue';

const TZ_SHORT = (() => {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
  } catch {
    return 'UTC';
  }
})();

function describeCron(expr: string): string | null {
  try {
    return cronstrue.toString(expr, { use24HourTimeFormat: true });
  } catch {
    return null;
  }
}

export default function RecurringDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const numericId = id !== undefined ? Number(id) : undefined;

  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();

  const detailQuery = useRecurringDetail(numericId);
  const jobsQuery = useRecurringJobs(numericId, page, pageSize);

  const enableJob = useEnableRecurringJob();
  const disableJob = useDisableRecurringJob();
  const triggerJob = useTriggerRecurringJob();
  const deleteJob = useDeleteRecurringJob();
  const { confirm, dialog: confirmDialog } = useConfirm();

  const detail = detailQuery.data;

  useEffect(() => {
    if (!detail) {
      usePageStore.getState().set({ title: 'Recurring job', subtitle: undefined });
      return;
    }

    const handleToggleEnabled = async () => {
      if (detail.disabledAt) {
        enableJob.mutate(detail.id);

        return;
      }
      const ok = await confirm({
        title: 'Disable recurring job?',
        description: `Future runs of "${detail.name}" will be paused until you re-enable it. Disabling a job that drives critical work (reconciliation, billing, etc.) can stall downstream processes — make sure this is intended.`,
        confirmLabel: 'Disable',
        destructive: true,
      });
      if (ok) {
        disableJob.mutate(detail.id);
      }
    };

    const handleTrigger = async () => {
      const ok = await confirm({
        title: 'Trigger recurring job now?',
        description: `A job will be enqueued immediately, on top of the normal cron schedule. Use this for manual reruns — not for backfills.`,
        confirmLabel: 'Trigger',
      });
      if (ok) {
        triggerJob.mutate(detail.id);
      }
    };

    const handleDelete = async () => {
      const ok = await confirm({
        title: 'Delete recurring job?',
        description: `Remove "${detail.name}"? Future runs will not be scheduled and history will be removed permanently. Existing in-flight jobs are unaffected. This cannot be undone.`,
        confirmLabel: 'Delete',
        destructive: true,
      });
      if (!ok) {
        return;
      }
      deleteJob.mutate(detail.id, { onSuccess: () => navigate('/recurring') });
    };

    usePageStore.getState().set({
      title: detail.name,
      subtitle: detail.cron,
      right: (
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={handleToggleEnabled}>
            {detail.disabledAt ? 'Enable' : 'Disable'}
          </Button>
          <Button variant="outline" size="sm" onClick={handleTrigger}>Trigger</Button>
          <Button variant="destructive" size="sm" onClick={handleDelete}>Delete</Button>
        </div>
      ),
    });
  }, [detail, enableJob, disableJob, triggerJob, deleteJob, navigate, confirm]);

  useEffect(() => {
    return () => usePageStore.getState().reset();
  }, []);

  if (detailQuery.error) return <ErrorState message={(detailQuery.error as Error).message} />;
  if (!detail) return <LoadingState />;

  const jobs = jobsQuery.data ?? null;

  return (
    <div className="flex flex-col gap-3 py-5">
      <div className="flex flex-wrap items-center gap-3 text-[12.5px]">
        <span className="font-mono bg-panel-2 border border-border px-2 py-0.5 rounded">{detail.cron}</span>
        {describeCron(detail.cron) && (
          <span className="text-text-mute italic">{describeCron(detail.cron)}</span>
        )}
        {detail.disabledAt ? (
          <span className="inline-flex items-center rounded-full bg-orange-100 px-2.5 py-0.5 text-xs font-medium text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">
            Disabled <RelativeTime date={detail.disabledAt} />
          </span>
        ) : (
          <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400">Enabled</span>
        )}
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
        <div className="flex flex-col gap-3">
          <Panel>
            <PanelHeader eyebrow="Details" />
            <div className="px-4 py-3">
              <dl className="grid grid-cols-[140px_1fr] gap-x-4 gap-y-2 text-[13px]">
                <dt className="warp-eyebrow text-text-mute">Type</dt>
                <dd>{shortType(detail.type)}</dd>
                <dt className="warp-eyebrow text-text-mute">Created</dt>
                <dd className="font-mono">{formatDateTime(detail.createdAt)}</dd>
                {detail.updatedAt && (
                  <>
                    <dt className="warp-eyebrow text-text-mute">Updated</dt>
                    <dd className="font-mono">{formatDateTime(detail.updatedAt)}</dd>
                  </>
                )}
                <dt className="warp-eyebrow text-text-mute" title={`Times shown in ${TZ_SHORT}`}>
                  Next execution
                </dt>
                <dd>{detail.nextExecution ? <RelativeTime date={detail.nextExecution} /> : 'N/A'}</dd>
                <dt className="warp-eyebrow text-text-mute" title={`Times shown in ${TZ_SHORT}`}>
                  Last execution
                </dt>
                <dd>{detail.lastExecution ? <RelativeTime date={detail.lastExecution} /> : 'Never'}</dd>
                <dt className="warp-eyebrow text-text-mute">ID</dt>
                <dd className="font-mono text-xs">{detail.id}</dd>
              </dl>
            </div>
          </Panel>

          {detail.message && (
            <Panel>
              <PanelHeader eyebrow="Payload" />
              <pre className="mono m-0 max-h-60 overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.7] text-text-dim">
                {detail.message}
              </pre>
            </Panel>
          )}
        </div>

        <div className="flex flex-col gap-3">
          <Panel className="overflow-hidden">
            <PanelHeader eyebrow="Execution history" action={<span className="text-[10.5px] text-text-mute">Retains last 100 runs</span>} />
            {jobs && jobs.items.length > 0 ? (
              <>
                <div className="overflow-x-auto">
                  <table className="w-full border-collapse">
                    <thead>
                      <tr className="bg-panel-2 border-b border-border">
                        <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Job</th>
                        <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">State</th>
                        <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Executed</th>
                      </tr>
                    </thead>
                    <tbody>
                      {jobs.items.map((entry, idx) => (
                        <tr key={entry.jobId ?? `log-${idx}`} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                          <td className="px-3.5 py-2 font-mono text-[12.5px]">
                            {entry.jobExists && entry.jobId ? (
                              <Link to={`/detail/${entry.jobId}`} className="text-primary hover:underline">{shortId(entry.jobId)}</Link>
                            ) : entry.jobId ? (
                              <span className="text-text-mute">{shortId(entry.jobId)}</span>
                            ) : (
                              <span className="text-text-mute">-</span>
                            )}
                          </td>
                          <td className="px-3.5 py-2 text-[12.5px]">
                            {entry.skipped ? (
                              <span className="inline-flex items-center rounded-full bg-orange-100 px-2 py-0.5 text-xs font-medium text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">Skipped</span>
                            ) : entry.jobExists && entry.currentState != null ? (
                              <StateBadge state={entry.currentState} />
                            ) : (
                              <span className="text-xs text-text-mute">Cleaned up</span>
                            )}
                          </td>
                          <td className="px-3.5 py-2 text-[12.5px]"><RelativeTime date={entry.createdAt} /></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <Pagination
                  page={page}
                  pageCount={jobs.pageCount}
                  onPageChange={setPage}
                  pageSize={pageSize}
                  onPageSizeChange={(size) => { setPageSize(size); setPage(0); }}
                />
              </>
            ) : (
              <div className="py-10 text-center text-[13px] text-text-mute">No executions yet</div>
            )}
          </Panel>
        </div>
      </div>
      {confirmDialog}
    </div>
  );
}
