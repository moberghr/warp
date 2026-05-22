import { useEffect, useState } from 'react';
import * as api from '@/api';
import type { WarpInfo } from '@/types';
import { cn } from '@/lib/utils';

function useUtcClock(): string {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), 1000);

    return () => window.clearInterval(id);
  }, []);

  return now.toISOString().substring(0, 19).replace('T', ' ');
}

export default function WarpStatusbar() {
  const [info, setInfo] = useState<WarpInfo | null>(null);
  const utc = useUtcClock();

  useEffect(() => {
    let cancelled = false;
    api
      .getInfo()
      .then((data) => {
        if (!cancelled) {
          setInfo(data);
        }
      })
      .catch(() => {
        // Statusbar is informational — silently hide on failure.
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div
      className={cn(
        'h-7 px-6 bg-panel-2/40 border-t border-border/60 shrink-0',
        'flex items-center gap-4 text-[10.5px] text-text-mute mono',
      )}
    >
      {info?.version && (
        <span>
          Warp <span className="text-text-dim">{info.version}</span>
        </span>
      )}
      {info?.provider && (
        <span>
          {info.provider}
          {info.host && (
            <>
              <span className="text-text-mute"> · Host: </span>
              <span className="text-text-dim">{info.host}</span>
            </>
          )}
          {info.database && (
            <>
              <span className="text-text-mute"> · DB: </span>
              <span className="text-text-dim">{info.database}</span>
            </>
          )}
          {info.schema && (
            <>
              <span className="text-text-mute"> · Schema: </span>
              <span className="text-text-dim">{info.schema}</span>
            </>
          )}
        </span>
      )}
      <span className="ml-auto">
        <span className="text-text-mute">UTC </span>
        <span className="text-text-dim">{utc}</span>
      </span>
    </div>
  );
}
