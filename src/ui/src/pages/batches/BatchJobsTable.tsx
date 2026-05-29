import { useEffect, useMemo, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Copy, ChevronDown } from 'lucide-react';
import { V2Tabs, type V2TabKind, type V2Tab } from '@/components/v2/V2Tabs';
import { shortId, shortType } from '@/utils/format';
import { State } from '@/types';
import type { JobModel, PagedList } from '@/types';
import * as api from '@/api';

const PAGE_SIZE = 20;

const TAB_ORDER: V2TabKind[] = [
  'awaiting',
  'scheduled',
  'enqueued',
  'processing',
  'completed',
  'failed',
  'deleted',
];

const TAB_LABELS: Record<V2TabKind, string> = {
  awaiting: 'Awaiting',
  scheduled: 'Scheduled',
  enqueued: 'Enqueued',
  processing: 'Processing',
  completed: 'Completed',
  failed: 'Failed',
  deleted: 'Deleted',
};

function pillClassForState(state: State): string {
  switch (state) {
    case State.Awaiting:   return 'awaiting';
    case State.Scheduled:  return 'scheduled';
    case State.Enqueued:   return 'enqueued';
    case State.Processing: return 'processing';
    case State.Completed:  return 'completed';
    case State.Failed:     return 'failed';
    case State.Deleted:    return 'deleted';
    default:               return '';
  }
}

function stateLabel(state: State): string {
  switch (state) {
    case State.Awaiting:   return 'Awaiting';
    case State.Scheduled:  return 'Scheduled';
    case State.Enqueued:   return 'Enqueued';
    case State.Processing: return 'Processing';
    case State.Completed:  return 'Completed';
    case State.Failed:     return 'Failed';
    case State.Deleted:    return 'Deleted';
    default:               return 'Unknown';
  }
}

function relativeFromNow(iso: string | null | undefined): string | null {
  if (!iso) return null;
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) return `${Math.max(1, Math.floor(diff / 1000))}s ago`;
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`;
  return `${Math.floor(diff / 86_400_000)}d ago`;
}

function formatTime(iso: string): string {
  // 13:05:53.916 style — short and matches the design's when-cell.
  const d = new Date(iso);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  const ss = String(d.getSeconds()).padStart(2, '0');
  const ms = String(d.getMilliseconds()).padStart(3, '0');

  return `${hh}:${mm}:${ss}.${ms}`;
}

function pickInitialActive(counts: Record<string, number>): V2TabKind {
  // Prefer the first non-empty tab, in TAB_ORDER. Falls back to 'awaiting'.
  for (const k of TAB_ORDER) {
    if ((counts[k] ?? 0) > 0) {
      return k;
    }
  }

  return 'awaiting';
}

export interface BatchJobsTableProps {
  parentId: string;
  /** Selects which API + cache key to use. Defaults to 'batch'. */
  parentKind?: 'batch' | 'message';
  onCountsUpdate?: (counts: Record<string, number>) => void;
}

export function BatchJobsTable({ parentId, parentKind = 'batch', onCountsUpdate }: BatchJobsTableProps) {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [active, setActive] = useState<V2TabKind | null>(null);

  const page = Number(searchParams.get('page') ?? '0') || 0;

  const fetchCounts = parentKind === 'message' ? api.getMessageJobCounts : api.getBatchJobCounts;
  const fetchJobs = parentKind === 'message' ? api.getMessageJobs : api.getBatchJobs;

  const countsQuery = useQuery({
    queryKey: [parentKind, parentId, 'jobs', 'counts'],
    queryFn: () => fetchCounts(parentId),
  });

  const counts = countsQuery.data ?? {};

  // Auto-select initial active tab once counts arrive.
  useEffect(() => {
    if (active === null && countsQuery.data) {
      setActive(pickInitialActive(countsQuery.data));
    }
  }, [active, countsQuery.data]);

  // Bubble counts up so the page header total can use them.
  useEffect(() => {
    if (countsQuery.data) {
      onCountsUpdate?.(countsQuery.data);
    }
  }, [countsQuery.data, onCountsUpdate]);

  const jobsQuery = useQuery<PagedList<JobModel>>({
    queryKey: [parentKind, parentId, 'jobs', active, page],
    queryFn: () => fetchJobs(parentId, page, PAGE_SIZE, active ?? undefined),
    enabled: active !== null,
    placeholderData: keepPreviousData,
  });

  const tabs = useMemo<V2Tab[]>(() => {
    return TAB_ORDER.map(k => ({
      kind: k,
      label: TAB_LABELS[k],
      count: counts[k] ?? 0,
    }));
  }, [counts]);

  const handleTabChange = (k: V2TabKind) => {
    if (k === active) {
      return;
    }
    setActive(k);
    // Reset page param on tab switch — but only when one is actually present, so we
    // don't fire an empty location change that causes ancestors using location/search
    // params to re-render unnecessarily.
    if (searchParams.has('page')) {
      const next = new URLSearchParams(searchParams);
      next.delete('page');
      setSearchParams(next, { replace: true });
    }
  };

  const totalForActive = active ? (counts[active] ?? 0) : 0;
  const items = jobsQuery.data?.items ?? [];
  const isEmpty = !jobsQuery.isLoading && items.length === 0;

  return (
    <div className="warp-table-wrap">
      {active && (
        <V2Tabs tabs={tabs} active={active} onChange={handleTabChange} />
      )}

      <div className="warp-table-toolbar">
        <div className="left">
          <span className="grouped">
            {jobsQuery.isLoading
              ? 'Loading…'
              : `Showing ${items.length} of ${totalForActive} ${totalForActive === 1 ? 'job' : 'jobs'}`}
          </span>
        </div>
      </div>

      <table className="warp-jt">
        <thead>
          <tr>
            <th style={{ width: 34, paddingRight: 0 }}>
              <span className="warp-checkbox" />
            </th>
            <th style={{ width: '34%' }}>
              <span className="inline-flex items-center gap-1">
                Type
                <ChevronDown size={10} style={{ opacity: 0.55 }} />
              </span>
            </th>
            <th style={{ width: 140 }}>Job ID</th>
            <th style={{ width: 240 }}>
              <span className="inline-flex items-center gap-1">
                Created
                <ChevronDown size={10} style={{ opacity: 0.55 }} />
              </span>
            </th>
            <th style={{ width: 130 }}>State</th>
          </tr>
        </thead>
        <tbody>
          {isEmpty && (
            <tr>
              <td colSpan={6} style={{ textAlign: 'center', padding: '32px 14px', color: 'var(--text-mute)' }}>
                No jobs in this state.
              </td>
            </tr>
          )}
          {items.map((j, i) => (
            <tr
              key={j.id}
              className="clickable"
              onClick={() => navigate(`/jobs/detail/${j.id}`)}
            >
              <td
                style={{ width: 34, paddingRight: 0 }}
                onClick={e => e.stopPropagation()}
              >
                <span className="warp-checkbox" />
              </td>
              <td>
                <div className="type-row">
                  <span className="ix">{page * PAGE_SIZE + i + 1}</span>
                  <span className="type-name">{shortType(j.type)}</span>
                </div>
              </td>
              <td>
                <div className="id-cell inline-flex items-center gap-1.5">
                  <span>{shortId(j.id)}</span>
                  <Copy
                    size={11}
                    className="opacity-0 transition-opacity group-hover:opacity-100"
                    style={{ color: 'var(--text-mute)' }}
                  />
                </div>
              </td>
              <td>
                <div className="when-cell">
                  <span>{formatTime(j.createTime)}</span>
                  <span className="rel">· {relativeFromNow(j.createTime)}</span>
                </div>
              </td>
              <td>
                <span className={`warp-pill ${pillClassForState(j.currentState)}`}>
                  {stateLabel(j.currentState)}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {(jobsQuery.data?.pageCount ?? 0) > 1 && (
        <Pager
          page={page}
          pageCount={jobsQuery.data!.pageCount}
          onChange={p => {
            const next = new URLSearchParams(searchParams);
            if (p === 0) {
              next.delete('page');
            } else {
              next.set('page', String(p));
            }
            setSearchParams(next, { replace: true });
          }}
        />
      )}
    </div>
  );
}

function Pager({
  page,
  pageCount,
  onChange,
}: {
  page: number;
  pageCount: number;
  onChange: (p: number) => void;
}) {
  return (
    <div
      style={{
        borderTop: '1px solid var(--border)',
        background: 'var(--panel-2)',
        padding: '10px 14px',
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        fontFamily: 'var(--font-mono)',
        fontSize: 11.5,
        color: 'var(--text-mute)',
      }}
    >
      <span>
        Page {page + 1} / {pageCount}
      </span>
      <div className="flex items-center gap-1">
        <button
          type="button"
          disabled={page === 0}
          onClick={() => onChange(Math.max(0, page - 1))}
          className="rounded px-2 py-1 text-[12px] disabled:opacity-40 hover:bg-accent"
          style={{ color: 'var(--text-dim)' }}
        >
          ← Prev
        </button>
        <button
          type="button"
          disabled={page >= pageCount - 1}
          onClick={() => onChange(Math.min(pageCount - 1, page + 1))}
          className="rounded px-2 py-1 text-[12px] disabled:opacity-40 hover:bg-accent"
          style={{ color: 'var(--text-dim)' }}
        >
          Next →
        </button>
      </div>
    </div>
  );
}
