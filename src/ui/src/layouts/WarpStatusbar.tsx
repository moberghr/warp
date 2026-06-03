import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import type { WarpInfo } from '@/types';
import { useDashboardStore } from '@/stores/dashboard';
import { useRealtimeStore } from '@/stores/realtime';

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
  const [offline, setOffline] = useState(false);
  const utc = useUtcClock();
  const stats = useDashboardStore((s) => s.stats);
  const realtimeStatus = useRealtimeStore((s) => s.status);

  const { data: servers } = useQuery({
    queryKey: ['servers-status'],
    queryFn: () => api.getServers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });

  useEffect(() => {
    let cancelled = false;
    api
      .getInfo()
      .then((data) => {
        if (!cancelled) {
          setInfo(data);
          setOffline(false);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setOffline(true);
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const serverCount = servers?.length ?? stats?.servers ?? 0;
  const workerCount = servers?.reduce((acc, s) => acc + (s.workers?.length ?? 0), 0) ?? 0;
  const live = realtimeStatus === 'connected' && !offline;

  return (
    <div className="soft-systembar">
      <span className="inline-flex items-center gap-1.5">
        <span className="font-semibold text-foreground tracking-wide">WARP</span>
        {info?.version && <span className="opacity-60">{info.version}</span>}
      </span>

      {info?.provider && (
        <>
          <span className="hair-v hidden sm:inline-block" aria-hidden />
          <span className="hidden sm:inline truncate">
            <span className="text-text-dim">{info.provider.toUpperCase()}</span>
            {info.host && (
              <>
                {' '}
                {info.database ? `${info.database}@${info.host}` : info.host}
              </>
            )}
          </span>
        </>
      )}

      {info?.schema && (
        <>
          <span className="hair-v hidden md:inline-block" aria-hidden />
          <span className="hidden md:inline">SCHEMA {info.schema}</span>
        </>
      )}

      <span className="ml-auto inline-flex items-center gap-2 tracking-wide shrink-0">
        {offline ? (
          <>
            <span className="inline-block h-1.5 w-1.5 rounded-full bg-warp-red" aria-hidden />
            <span className="font-semibold text-warp-red">OFFLINE</span>
          </>
        ) : (
          <>
            <span
              className="relative inline-flex h-1.5 w-1.5 items-center justify-center"
              aria-hidden
            >
              <span className="absolute inset-0 rounded-full bg-brand opacity-25" />
              <span className="relative inline-block h-1.5 w-1.5 rounded-full bg-brand" />
            </span>
            <span className="font-semibold text-brand">{live ? 'LIVE' : 'IDLE'}</span>
          </>
        )}
        <span className="opacity-60 hidden sm:inline">·</span>
        <span className="hidden sm:inline">
          {serverCount} SRV · {workerCount} WRK
        </span>
        <span className="opacity-60">·</span>
        <span><span className="hidden sm:inline">UTC </span>{utc.slice(11)}</span>
      </span>
    </div>
  );
}
