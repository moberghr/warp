import { Link } from 'react-router-dom';
import { useDashboardStore } from '@/stores/dashboard';

type StateDef = {
  slug: string;
  label: string;
  accent: string;
  accentBg: string;
  countKey: keyof NonNullable<ReturnType<typeof useDashboardStore.getState>['stats']> | null;
};

const STATES: StateDef[] = [
  { slug: 'enqueued',   label: 'Enqueued',   accent: 'text-warp-blue',   accentBg: 'bg-warp-blue-soft',   countKey: 'created' },
  { slug: 'scheduled',  label: 'Scheduled',  accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'scheduled' },
  { slug: 'processing', label: 'Processing', accent: 'text-warp-purple', accentBg: 'bg-warp-purple-soft', countKey: 'processing' },
  { slug: 'completed',  label: 'Completed',  accent: 'text-warp-green',  accentBg: 'bg-warp-green-soft',  countKey: 'completed' },
  { slug: 'failed',     label: 'Failed',     accent: 'text-warp-red',    accentBg: 'bg-warp-red-soft',    countKey: 'failed' },
  { slug: 'awaiting',   label: 'Awaiting',   accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'awaiting' },
  { slug: 'deleted',    label: 'Deleted',    accent: 'text-text-mute',   accentBg: 'bg-panel-2',          countKey: 'deleted' },
];

interface JobsStateRailProps {
  active: string;
}

export function JobsStateRail({ active }: JobsStateRailProps) {
  const stats = useDashboardStore((s) => s.stats);

  return (
    <aside className="bg-background lg:border-r border-border p-3 lg:w-[200px] w-full shrink-0">
      <div className="warp-eyebrow px-2 pb-2">Job state</div>
      <nav className="flex flex-col gap-px">
        {STATES.map((s) => {
          const isActive = active === s.slug;
          const count = s.countKey && stats ? (stats[s.countKey] as number) ?? 0 : 0;

          return (
            <Link
              key={s.slug}
              to={`/jobs/${s.slug}`}
              className={`flex items-center justify-between px-2.5 py-1.5 rounded-md text-[13px] font-medium transition-colors border-l-2 ${
                isActive
                  ? `bg-panel-2 ${s.accent} border-current`
                  : 'text-text-dim border-transparent hover:bg-panel-2/60'
              }`}
            >
              <span>{s.label}</span>
              <span
                className={`mono text-[10.5px] font-semibold px-1.5 py-0.5 rounded-full ${
                  isActive
                    ? `${s.accentBg} ${s.accent}`
                    : s.slug === 'failed' && count > 0
                      ? `${s.accentBg} ${s.accent}`
                      : 'bg-panel-2 text-text-mute border border-border'
                }`}
              >
                {count.toLocaleString()}
              </span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
