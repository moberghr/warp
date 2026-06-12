import { Skeleton } from '@/components/ui/skeleton';
import { Panel } from '@/components/v2/Panel';

export function DetailSkeleton() {
  return (
    <div className="flex flex-col gap-3 p-5">
      <div className="flex items-center gap-4">
        <Skeleton className="h-7 w-48" />
        <Skeleton className="h-5 w-20 rounded-full" />
        <Skeleton className="h-4 w-32" />
        <div className="flex-1" />
        <Skeleton className="h-8 w-20" />
        <Skeleton className="h-8 w-20" />
      </div>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
        <Panel>
          <div className="border-b border-border px-4 py-2.5">
            <Skeleton className="h-4 w-24" />
          </div>
          <div className="p-4 space-y-2">
            <Skeleton className="h-4 w-3/4" />
            <Skeleton className="h-4 w-2/3" />
            <Skeleton className="h-4 w-1/2" />
            <Skeleton className="h-4 w-3/5" />
          </div>
        </Panel>
        <div className="space-y-3">
          <Skeleton className="h-4 w-20" />
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className="border border-border rounded-md p-4 bg-panel-2/40 space-y-2">
              <div className="flex justify-between">
                <Skeleton className="h-4 w-24" />
                <Skeleton className="h-3 w-16" />
              </div>
              <Skeleton className="h-3 w-full" />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
