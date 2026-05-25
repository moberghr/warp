import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Briefcase, Clock, Layers, Loader, Mail, XCircle } from 'lucide-react';
import { StatCard } from '@/components/v2/StatCard';
import { DashboardSkeleton } from '@/components/skeletons/DashboardSkeleton';
import { useDashboardStore } from '@/stores/dashboard';
import { usePageStore } from '@/stores/page';
import { useInfo } from '@/api/hooks/useInfo';
import { ThroughputChart } from './ThroughputChart';
import { HistoryChart } from './HistoryChart';

export default function DashboardPage() {
  const stats = useDashboardStore((s) => s.stats);
  const { data: info } = useInfo();

  useEffect(() => {
    // Subtitle: "<schema> · <provider>" if we have it, otherwise just "live".
    const parts: string[] = [];
    if (info?.schema) {
      parts.push(info.schema);
    }
    if (info?.provider) {
      parts.push(info.provider);
    }
    parts.push('live');

    usePageStore.getState().set({
      title: 'Dashboard',
      subtitle: parts.join(' · '),
      right: undefined,
    });

    return () => {
      usePageStore.getState().reset();
    };
  }, [info?.schema, info?.provider]);

  if (!stats) {
    return <DashboardSkeleton />;
  }

  return (
    <div className="flex flex-col gap-3">
      {/* 6-up stat row */}
      <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 lg:grid-cols-6">
        <StatCard
          label="Enqueued"
          value={stats.created}
          icon={Briefcase}
          href="/jobs/enqueued"
          as={Link}
        />
        <StatCard
          label="Processing"
          value={stats.processing}
          icon={Loader}
          accentClass={stats.processing > 0 ? 'text-warp-purple' : undefined}
          accentColor={stats.processing > 0 ? 'var(--warp-purple)' : undefined}
          href="/jobs/processing"
          as={Link}
        />
        <StatCard
          label="Scheduled"
          value={stats.scheduled}
          icon={Clock}
          href="/jobs/scheduled"
          as={Link}
        />
        <StatCard
          label="Failed"
          value={stats.failed}
          icon={XCircle}
          accentClass={stats.failed > 0 ? 'text-warp-red' : undefined}
          accentColor={stats.failed > 0 ? 'var(--warp-red)' : undefined}
          href="/jobs/failed"
          as={Link}
        />
        <StatCard
          label="Messages"
          value={stats.messages}
          icon={Mail}
          href="/messages/enqueued"
          as={Link}
          sub={
            stats.messagesFailed > 0
              ? `${stats.messagesFailed} failed`
              : undefined
          }
        />
        <StatCard
          label="Batches"
          value={stats.batchesProcessing}
          icon={Layers}
          href="/batches/processing"
          as={Link}
          sub={
            stats.batchesProcessing > 0
              ? `${stats.batchesProcessing} in progress`
              : undefined
          }
        />
      </div>

      <ThroughputChart />
      <HistoryChart />
    </div>
  );
}
