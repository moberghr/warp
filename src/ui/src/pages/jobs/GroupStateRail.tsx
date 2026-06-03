import { Link } from 'react-router-dom';
import { useDashboardStore } from '@/stores/dashboard';
import type { DashboardStatistics } from '@/types';

type StateDef = {
  slug: string | null;
  label: string;
  accent: string;
  accentBg: string;
  countKey: keyof DashboardStatistics | null;
};

const MESSAGES_STATES: StateDef[] = [
  { slug: null,         label: 'All',        accent: 'text-foreground',  accentBg: 'bg-panel-2',          countKey: 'messages' },
  { slug: 'awaiting',   label: 'Awaiting',   accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'messagesAwaiting' },
  { slug: 'scheduled',  label: 'Scheduled',  accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'messagesScheduled' },
  { slug: 'enqueued',   label: 'Enqueued',   accent: 'text-warp-blue',   accentBg: 'bg-warp-blue-soft',   countKey: 'messagesEnqueued' },
  { slug: 'processing', label: 'Processing', accent: 'text-warp-purple', accentBg: 'bg-warp-purple-soft', countKey: 'messagesProcessing' },
  { slug: 'completed',  label: 'Completed',  accent: 'text-warp-green',  accentBg: 'bg-warp-green-soft',  countKey: 'messagesCompleted' },
  { slug: 'failed',     label: 'Failed',     accent: 'text-warp-red',    accentBg: 'bg-warp-red-soft',    countKey: 'messagesFailed' },
  { slug: 'deleted',    label: 'Deleted',    accent: 'text-text-mute',   accentBg: 'bg-panel-2',          countKey: 'messagesDeleted' },
];

const BATCHES_STATES: StateDef[] = [
  { slug: null,         label: 'All',        accent: 'text-foreground',  accentBg: 'bg-panel-2',          countKey: 'batches' },
  { slug: 'awaiting',   label: 'Awaiting',   accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'batchesAwaiting' },
  { slug: 'scheduled',  label: 'Scheduled',  accent: 'text-warp-amber',  accentBg: 'bg-warp-amber-soft',  countKey: 'batchesScheduled' },
  { slug: 'enqueued',   label: 'Enqueued',   accent: 'text-warp-blue',   accentBg: 'bg-warp-blue-soft',   countKey: 'batchesEnqueued' },
  { slug: 'processing', label: 'Processing', accent: 'text-warp-purple', accentBg: 'bg-warp-purple-soft', countKey: 'batchesProcessing' },
  { slug: 'completed',  label: 'Completed',  accent: 'text-warp-green',  accentBg: 'bg-warp-green-soft',  countKey: 'batchesCompleted' },
  { slug: 'failed',     label: 'Failed',     accent: 'text-warp-red',    accentBg: 'bg-warp-red-soft',    countKey: 'batchesFailed' },
  { slug: 'deleted',    label: 'Deleted',    accent: 'text-text-mute',   accentBg: 'bg-panel-2',          countKey: 'batchesDeleted' },
];

interface GroupStateRailProps {
  kind: 'messages' | 'batches';
  active: string | undefined;
}

export function GroupStateRail({ kind, active }: GroupStateRailProps) {
  const stats = useDashboardStore((s) => s.stats);
  const states = kind === 'messages' ? MESSAGES_STATES : BATCHES_STATES;
  const eyebrow = kind === 'messages' ? 'Message state' : 'Batch state';
  const basePath = kind === 'messages' ? '/messages' : '/batches';

  return (
    <aside className="bg-background lg:border-r border-border p-3 lg:w-[200px] w-full shrink-0">
      <div className="warp-eyebrow px-2 pb-2">{eyebrow}</div>
      <nav className="flex flex-col gap-px">
        {states.map((s) => {
          const isActive = (active ?? null) === s.slug;
          const count = s.countKey && stats ? (stats[s.countKey] as number) ?? 0 : 0;
          const to = s.slug ? `${basePath}/${s.slug}` : basePath;

          return (
            <Link
              key={s.slug ?? 'all'}
              to={to}
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
                    : 'bg-panel-2 text-text-mute border border-border'
                }`}
                aria-label={s.countKey ? undefined : 'count unavailable'}
              >
                {s.countKey ? count.toLocaleString() : '—'}
              </span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
