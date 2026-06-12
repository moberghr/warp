import { Skeleton } from '@/components/ui/skeleton';
import { Panel } from '@/components/v2/Panel';

interface TableSkeletonProps {
  rows?: number;
  columns?: number;
  headers?: string[];
}

export function TableSkeleton({ rows = 8, columns, headers }: TableSkeletonProps) {
  const colCount = headers?.length ?? columns ?? 5;

  return (
    <Panel className="overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full border-collapse">
          <thead>
            <tr className="bg-panel-2 border-b border-border">
              {Array.from({ length: colCount }).map((_, i) => (
                <th key={i} className="warp-eyebrow text-left px-3.5 py-2.5 text-text-mute font-semibold">
                  {headers?.[i] ?? ''}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {Array.from({ length: rows }).map((_, r) => (
              <tr key={r} className="border-b border-border last:border-b-0">
                {Array.from({ length: colCount }).map((__, c) => (
                  <td key={c} className="px-3.5 py-2">
                    <Skeleton className="h-4 w-full max-w-[140px]" />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Panel>
  );
}
