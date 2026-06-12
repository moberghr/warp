import { Skeleton } from '@/components/ui/skeleton';
import { Panel } from '@/components/v2/Panel';

export function DashboardSkeleton() {
  return (
    <div className="flex flex-col gap-3 p-5">
      <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 lg:grid-cols-6">
        {Array.from({ length: 6 }).map((_, i) => (
          <Panel key={i}>
            <div className="p-4">
              <div className="flex items-center justify-between">
                <div className="flex-1 space-y-2">
                  <Skeleton className="h-3 w-16" />
                  <Skeleton className="h-7 w-12" />
                </div>
                <Skeleton className="h-5 w-5 rounded-full" />
              </div>
              <Skeleton className="h-8 w-full mt-3" />
            </div>
          </Panel>
        ))}
      </div>
      <Panel>
        <div className="border-b border-border px-4 py-2.5">
          <Skeleton className="h-4 w-40" />
        </div>
        <div className="p-4">
          <Skeleton className="h-[200px] w-full" />
        </div>
      </Panel>
      <Panel>
        <div className="border-b border-border px-4 py-2.5 flex items-center justify-between">
          <Skeleton className="h-4 w-32" />
          <Skeleton className="h-4 w-24" />
        </div>
        <div className="p-4">
          <Skeleton className="h-[180px] w-full" />
        </div>
      </Panel>
    </div>
  );
}
