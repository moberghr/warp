import { useCallback, useEffect, useState } from 'react';
import { Navigate, useLocation, useParams } from 'react-router-dom';
import { ErrorState } from '@/components/PageState';
import { DetailSkeleton } from '@/components/skeletons/DetailSkeleton';
import { useJobDetail } from '@/api/hooks/useJobs';
import { usePageStore } from '@/stores/page';
import { detailPath, shortId } from '@/utils/format';
import { stateName } from '@/utils/format';
import { useRealtimeRefetch } from '@/hooks/useRealtimeRefetch';
import { JobDetailStandard } from './JobDetailStandard';
import { BatchDetailPage } from '@/pages/batches/BatchDetailPage';

function kindLabel(kind: number) {
  if (kind === 3) {
    return 'Batch';
  }
  if (kind === 2) {
    return 'Message';
  }

  return 'Job';
}

export default function DetailPage() {
  const { id } = useParams<{ id: string }>();
  const location = useLocation();
  const [jobCounts, setJobCounts] = useState<Record<string, number>>({});

  const query = useJobDetail(id);
  const setPage = usePageStore(s => s.set);
  const resetPage = usePageStore(s => s.reset);

  useRealtimeRefetch('JobFinalized', () => {
    void query.refetch();
  });

  const job = query.data;

  useEffect(() => {
    if (!job) {
      setPage({ title: 'Detail', subtitle: undefined });

      return;
    }
    setPage({
      title: `${kindLabel(job.kind)} detail`,
      subtitle: `${kindLabel(job.kind)} › ${stateName(job.currentState)} › ${shortId(job.id)}`,
    });
  }, [job, setPage]);

  useEffect(() => {
    return () => resetPage();
  }, [resetPage]);

  const handleCountsUpdate = useCallback((counts: Record<string, number>) => {
    setJobCounts(counts);
  }, []);

  if (query.error) {
    return <ErrorState message={(query.error as Error).message} />;
  }
  if (!job) {
    return <DetailSkeleton />;
  }

  const canonical = detailPath(job.id, job.kind);
  if (location.pathname !== canonical) {
    return <Navigate to={canonical} replace />;
  }

  const systemEvents = job.logs.filter(l => l.eventType !== 'Log' && l.eventType !== 'Progress').reverse();
  const handlerLogs = job.logs.filter(l => l.eventType === 'Log');

  const progressByName = new Map<string, number>();
  for (const log of job.logs) {
    if (log.eventType !== 'Progress' || log.value == null) {
      continue;
    }
    progressByName.set(log.name ?? '', log.value);
  }
  const reportedBars = Array.from(progressByName.entries());

  const isBatch = job.kind === 3;

  if (isBatch) {
    return <BatchDetailPage job={job} systemEvents={systemEvents} />;
  }

  return (
    <JobDetailStandard
      job={job}
      systemEvents={systemEvents}
      handlerLogs={handlerLogs}
      reportedBars={reportedBars}
      jobCounts={jobCounts}
      onCountsUpdate={handleCountsUpdate}
    />
  );
}
