import { useState } from 'react';
import { BatchJobsTable } from '@/pages/batches/BatchJobsTable';
import type { UnifiedJobDetailModel } from '@/types';

interface RelatedJobsSectionProps {
  job: UnifiedJobDetailModel;
  onCountsUpdate: (counts: Record<string, number>) => void;
}

export function RelatedJobsSection({ job, onCountsUpdate }: RelatedJobsSectionProps) {
  const isBatch = job.kind === 3;
  const parentKind = isBatch ? 'batch' : 'message';
  const [counts, setCounts] = useState<Record<string, number>>({});

  const total = Object.values(counts).reduce((a, b) => a + b, 0);

  return (
    <section className="mt-6 flex flex-col gap-[14px]">
      <div className="warp-section-head">
        <div className="warp-section-title">
          <h2>Jobs</h2>
          <span className="ct">({total || job.totalJobs})</span>
        </div>
      </div>
      <BatchJobsTable
        key={job.id}
        parentId={job.id}
        parentKind={parentKind}
        onCountsUpdate={c => {
          setCounts(c);
          onCountsUpdate(c);
        }}
      />
    </section>
  );
}
