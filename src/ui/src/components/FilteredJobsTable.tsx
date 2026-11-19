import { useState, useEffect, useCallback, useLayoutEffect, useRef } from 'react';
import { Link } from 'react-router-dom';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { StateBadge } from '@/components/StateBadge';
import { Pagination } from '@/components/Pagination';
import { shortType, shortId } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { State } from '@/types';
import type { JobModel, PagedList } from '@/types';
import { useRealtimeRefetch } from '@/hooks/useRealtimeRefetch';
import * as api from '@/api';

const stateItems = [
  { key: 'awaiting', label: 'Awaiting', color: 'bg-orange-100 text-orange-700 dark:bg-orange-900 dark:text-orange-300' },
  { key: 'enqueued', label: 'Enqueued', color: 'bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300' },
  { key: 'processing', label: 'Processing', color: 'bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-300' },
  { key: 'completed', label: 'Completed', color: 'bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300' },
  { key: 'failed', label: 'Failed', color: 'bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300' },
];

interface FilteredJobsTableProps {
  title: string;
  fetchJobs: (page: number, pageSize: number, state?: string) => Promise<PagedList<JobModel>>;
  fetchCounts: () => Promise<Record<string, number>>;
  onCountsUpdate?: (counts: Record<string, number>) => void;
}

export function FilteredJobsTable({ title, fetchJobs, fetchCounts, onCountsUpdate }: FilteredJobsTableProps) {
  const [selectedState, setSelectedState] = useState<string | null>(null);
  const [data, setData] = useState<PagedList<JobModel> | null>(null);
  const [counts, setCounts] = useState<Record<string, number>>({});
  const [countsError, setCountsError] = useState(false);
  const [page, setPage] = useState(0);
  const [refreshKey, setRefreshKey] = useState(0);
  const pageSize = 10;
  const initializedRef = useRef(false);
  const fetchJobsRef = useRef(fetchJobs);
  const fetchCountsRef = useRef(fetchCounts);
  const onCountsUpdateRef = useRef(onCountsUpdate);
  // Keep the latest callbacks without re-subscribing; declared before the fetch effects so they see the new values.
  useLayoutEffect(() => {
    fetchJobsRef.current = fetchJobs;
    fetchCountsRef.current = fetchCounts;
    onCountsUpdateRef.current = onCountsUpdate;
  });

  const updateCounts = useCallback(() => {
    fetchCountsRef.current().then(c => {
      setCounts(c);
      setCountsError(false);
      onCountsUpdateRef.current?.(c);
      if (!initializedRef.current) {
        initializedRef.current = true;
        const best = stateItems.reduce((max, item) => (c[item.key] ?? 0) > (c[max.key] ?? 0) ? item : max, stateItems[0]);
        setSelectedState(best.key);
      }
    }).catch(() => setCountsError(true));
  }, []);

  useEffect(() => { updateCounts(); }, [updateCounts]);
  useRealtimeRefetch('JobFinalized', updateCounts);

  const fetch = useCallback(async () => {
    if (!selectedState) return;
    try {
      const result = await fetchJobsRef.current(page, pageSize, selectedState);
      setData(result);
    } catch {
      // ignore
    }
  }, [page, selectedState]);

  useEffect(() => { fetch(); }, [fetch, refreshKey]);

  return (
    <div>
      <div className="flex items-center gap-2 mb-3">
        <h2 className="text-lg font-semibold">{title}</h2>
        {Object.keys(counts).length > 0 && (
          <span className="text-sm text-muted-foreground">({Object.values(counts).reduce((a, b) => a + b, 0)})</span>
        )}
      </div>
      {countsError && (
        <div className="text-xs text-destructive bg-destructive/10 border border-destructive/20 rounded-md px-3 py-1.5 mb-2">
          Unable to refresh counts — showing last known data
        </div>
      )}
      <div className="flex gap-4">
        {/* Vertical state sidebar */}
        <nav className="w-44 shrink-0 space-y-1">
          {stateItems.map((item) => {
            const isActive = selectedState === item.key;
            return (
              <button
                key={item.label}
                onClick={() => {
                  if (isActive) {
                    setRefreshKey(k => k + 1);
                  } else {
                    setSelectedState(item.key);
                    setPage(0);
                  }
                }}
                className={`w-full flex items-center justify-between px-3 py-2 rounded-md text-sm transition-colors text-left ${
                  isActive
                    ? 'bg-accent text-accent-foreground font-medium'
                    : 'text-muted-foreground hover:bg-accent/50'
                }`}
              >
                <span>{item.label}</span>
                <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                  (counts[item.key] ?? 0) > 0 ? item.color : 'text-muted-foreground/50'
                }`}>
                  {counts[item.key] ?? 0}
                </span>
              </button>
            );
          })}
        </nav>

        {/* Jobs table */}
        <div className="flex-1">
          {data && data.items.length > 0 ? (
            <>
              <div className="rounded-md border">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead className="w-[80px]">ID</TableHead>
                      <TableHead>Type</TableHead>
                      <TableHead>Handler</TableHead>
                      <TableHead className="w-[100px] text-right">State</TableHead>
                      <TableHead className="w-[120px] text-right">Created</TableHead>
                      <TableHead className="w-[80px]" />
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.items.map((job) => (
                      <TableRow key={job.id}>
                        <TableCell className="font-mono text-xs">
                          <Link to={`/detail/${job.id}`} className="text-primary hover:underline">{shortId(job.id)}</Link>
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground truncate max-w-[200px]">{shortType(job.type)}</TableCell>
                        <TableCell className="text-sm text-muted-foreground truncate max-w-[200px]">{job.handlerType ? shortType(job.handlerType) : '—'}</TableCell>
                        <TableCell className="text-right"><StateBadge state={job.currentState} /></TableCell>
                        <TableCell className="text-sm text-muted-foreground text-right"><RelativeTime date={job.createTime} /></TableCell>
                        <TableCell className="text-right">
                          {job.currentState === State.Failed && (
                            <Button variant="outline" size="sm" className="h-7 text-xs" onClick={async () => { await api.requeueJob(job.id); setRefreshKey(k => k + 1); }}>
                              Requeue
                            </Button>
                          )}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
              {data.pageCount > 1 && (
                <Pagination page={page} pageCount={data.pageCount} onPageChange={setPage} />
              )}
            </>
          ) : (
            <div className="text-sm text-muted-foreground py-4 text-center">
              {data ? 'No jobs found' : 'Loading...'}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
