export interface ProgressBreakdown {
  awaiting: number;
  processing: number;
  completed: number;
  failed: number;
}

export interface ProgressCardProps {
  total: number;
  breakdown: ProgressBreakdown;
}

export function ProgressCard({ total, breakdown }: ProgressCardProps) {
  const done = breakdown.completed + breakdown.failed;
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;

  // Build segments: completed segs first, then failed, processing, awaiting.
  const segs: { cls: string }[] = [];
  for (let i = 0; i < breakdown.completed; i++) {
    segs.push({ cls: 'seg done' });
  }
  for (let i = 0; i < breakdown.failed; i++) {
    segs.push({ cls: 'seg fail' });
  }
  for (let i = 0; i < breakdown.processing; i++) {
    segs.push({ cls: 'seg processing' });
  }
  for (let i = 0; i < breakdown.awaiting; i++) {
    segs.push({ cls: 'seg awaiting' });
  }
  // If counts don't match total (rare), pad with empty.
  while (segs.length < total) {
    segs.push({ cls: 'seg' });
  }

  return (
    <div className="warp-inner-card">
      <div className="warp-progress-head">
        <span className="warp-card-label">Progress</span>
        <span className="warp-progress-stats">
          <span className="big">
            {done}
            <span className="denom">/{total}</span>
          </span>
          <span className="pct">· {pct}%</span>
        </span>
      </div>
      <div className="warp-progress-bar">
        {segs.map((s, i) => (
          <div key={i} className={s.cls} />
        ))}
      </div>
      <div className="warp-progress-breakdown">
        <span className="pb-item">
          <span className="sw awaiting" />
          <span>Awaiting</span>
          <span className="num">{breakdown.awaiting}</span>
        </span>
        <span className="pb-item">
          <span className="sw processing" />
          <span>Processing</span>
          <span className="num">{breakdown.processing}</span>
        </span>
        <span className="pb-item">
          <span className="sw completed" />
          <span>Completed</span>
          <span className="num">{breakdown.completed}</span>
        </span>
        <span className="pb-item">
          <span className="sw failed" />
          <span>Failed</span>
          <span className="num">{breakdown.failed}</span>
        </span>
      </div>
    </div>
  );
}
