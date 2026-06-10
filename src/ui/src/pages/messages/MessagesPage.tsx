import { useEffect } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Panel } from '@/components/v2/Panel';
import { StateBadge } from '@/components/StateBadge';
import { Pagination } from '@/components/Pagination';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePageParam } from '@/hooks/usePageParam';
import { usePageStore } from '@/stores/page';
import { useMessagesList } from '@/api/hooks/useMessages';
import { GroupStateRail } from '@/pages/jobs/GroupStateRail';

export default function MessagesPage() {
  const { state } = useParams<{ state?: string }>();
  const [page, setPage] = usePageParam();
  const [pageSize, setPageSize] = usePersistedPageSize();

  const query = useMessagesList(state, page, pageSize);

  useEffect(() => {
    const title = state ? `${state.charAt(0).toUpperCase() + state.slice(1)} Messages` : 'Messages';
    usePageStore.getState().set({
      title,
      subtitle: 'Pub-sub fan-out — one publish, many handlers',
    });
    return () => usePageStore.getState().reset();
  }, [state]);

  const data = query.data;

  return (
    <div className="flex flex-col lg:flex-row">
      <GroupStateRail kind="messages" active={state} />
      <div className="flex-1 p-5 min-w-0 flex flex-col gap-3">
      {query.error ? (
        <ErrorState message={(query.error as Error).message} />
      ) : !data ? (
        <LoadingState />
      ) : (
      <>
      <Panel className="overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="bg-panel-2 border-b border-border">
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold w-[100px]">ID</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Type</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Queue</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">State</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Jobs</th>
                <th className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-3.5 py-8 text-center text-[12.5px] text-text-mute">
                    No messages found
                  </td>
                </tr>
              ) : (
                data.items.map((msg) => (
                  <tr key={msg.id} className="border-b border-border last:border-b-0 hover:bg-panel-2/60">
                    <td className="px-3.5 py-2 font-mono text-[12.5px]">
                      <Link to={`/messages/detail/${msg.id}`} className="text-primary hover:underline">
                        {shortId(msg.id)}
                      </Link>
                    </td>
                    <td className="px-3.5 py-2 text-[12.5px]">{msg.type ? shortType(msg.type) : '—'}</td>
                    <td className="px-3.5 py-2 text-[12.5px]">{msg.queue ?? '—'}</td>
                    <td className="px-3.5 py-2 text-[12.5px]"><StateBadge state={msg.currentState} /></td>
                    <td className="px-3.5 py-2 text-[12.5px]">{msg.totalJobs}</td>
                    <td className="px-3.5 py-2 text-[12.5px] text-text-mute">
                      <RelativeTime date={msg.createTime} />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <Pagination page={page} pageCount={data.pageCount} onPageChange={setPage} pageSize={pageSize} onPageSizeChange={(size) => { setPageSize(size); setPage(0); }} totalCount={data.totalCount} />
      </>
      )}
      </div>
    </div>
  );
}
