import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Panel } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePageParam } from '@/hooks/usePageParam';
import { usePageStore } from '@/stores/page';
import {
  useRecurringList,
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

export default function RecurringPage() {
  const [page, setPage] = usePageParam();
  const [pageSize, setPageSize] = usePersistedPageSize();
  const query = useRecurringList(page, pageSize);

  const enableJob = useEnableRecurringJob();
  const disableJob = useDisableRecurringJob();
  const triggerJob = useTriggerRecurringJob();
  const deleteJob = useDeleteRecurringJob();
  const { confirm, dialog: confirmDialog } = useConfirm();

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Recurring Jobs',
      subtitle: 'Cron-scheduled jobs that re-enqueue automatically',
    });
    return () => usePageStore.getState().reset();
  }, []);

  if (query.error) return <ErrorState message={(query.error as Error).message} />;
  if (!query.data) return <LoadingState />;

  const data = query.data;

  return (
    <div className="flex flex-col gap-3 py-5">
      <Panel className="overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Name</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Cron</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Type</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Status</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold" title={`Times shown in ${TZ_SHORT}`}>Next execution</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Last execution</th>
                <th className="warp-eyebrow text-right px-3.5 py-2.5 text-text-mute font-semibold">Actions</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-3.5 py-8 text-center text-[12.5px] text-text-mute">
                    No recurring jobs found
                  </td>
                </tr>
              ) : (
                data.items.map((rj) => (
                  <tr key={rj.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                    <td className="px-3.5 py-2 text-[12.5px] font-medium">
                      <Link to={`/recurring/${rj.id}`} className="text-primary hover:underline">{rj.name}</Link>
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">
                      <div className="font-mono">{rj.cron}</div>
                      {describeCron(rj.cron) && (
                        <div className="text-[11px] text-text-mute mt-0.5">{describeCron(rj.cron)}</div>
                      )}
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">{rj.type.split(',')[0].split('.').pop()}</td>
                    <td className="px-3.5 py-2 text-[12.5px]">
                      {rj.disabledAt ? (
                        <span className="inline-flex items-center rounded-full bg-warp-amber-soft px-2 py-0.5 text-xs font-medium text-warp-amber">Disabled</span>
                      ) : (
                        <span className="inline-flex items-center rounded-full bg-warp-green-soft px-2 py-0.5 text-xs font-medium text-warp-green">Enabled</span>
                      )}
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">
                      {rj.nextExecution ? <RelativeTime date={rj.nextExecution} /> : 'N/A'}
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px] text-text-mute">
                      {rj.lastExecution ? <RelativeTime date={rj.lastExecution} /> : 'Never'}
                    </td>
                    <td className="px-3.5 py-2 text-right text-[12.5px]">
                      {rj.disabledAt ? (
                        <Button variant="ghost" size="sm" onClick={() => enableJob.mutate(rj.id)}>
                          Enable
                        </Button>
                      ) : (
                        <Button variant="ghost" size="sm" onClick={() => disableJob.mutate(rj.id)}>
                          Disable
                        </Button>
                      )}
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={async () => {
                          const ok = await confirm({
                            title: 'Trigger recurring job now?',
                            description: `A job will be enqueued immediately, on top of the normal cron schedule. Use this for manual reruns — not for backfills.`,
                            confirmLabel: 'Trigger',
                          });
                          if (ok) {
                            triggerJob.mutate(rj.id);
                          }
                        }}
                      >
                        Trigger
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-destructive"
                        onClick={async () => {
                          const ok = await confirm({
                            title: 'Delete recurring job?',
                            description: `Remove "${rj.name}"? Future runs will not be scheduled and history will be removed permanently. Existing in-flight jobs are unaffected. This cannot be undone.`,
                            confirmLabel: 'Delete',
                            destructive: true,
                          });
                          if (ok) {
                            deleteJob.mutate(rj.id);
                          }
                        }}
                      >
                        Delete
                      </Button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <Pagination page={page} pageCount={data.pageCount} onPageChange={setPage} pageSize={pageSize} onPageSizeChange={(size) => { setPageSize(size); setPage(0); }} totalCount={data.totalCount} />
      {confirmDialog}
    </div>
  );
}
