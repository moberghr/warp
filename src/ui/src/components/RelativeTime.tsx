import { useEffect, useState } from 'react';
import { formatRelativeTime, formatDateTimeExact } from '@/utils/format';

export function RelativeTime({ date }: { date: string }) {
  // Coarse tick so the "x ago" label stays fresh on pages whose data
  // doesn't change — React Query skips re-renders when refetches return
  // identical data, so without this the label freezes.
  const [, setTick] = useState(0);
  useEffect(() => {
    const id = setInterval(() => setTick((t) => t + 1), 30_000);

    return () => clearInterval(id);
  }, []);

  return (
    <span>
      {formatDateTimeExact(date)} <span className="text-muted-foreground">({formatRelativeTime(date)})</span>
    </span>
  );
}
