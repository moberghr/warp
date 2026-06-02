import { NavLink } from 'react-router-dom';
import { Zap, Menu } from 'lucide-react';
import { useDashboardStore } from '@/stores/dashboard';
import { useInfo } from '@/api/hooks/useInfo';
import { cn } from '@/lib/utils';
import type { WarpNavItem, NavBadge } from './warpNavItems';

interface Props {
  items: WarpNavItem[];
  onMenuClick?: () => void;
}

function Badge({ badge }: { badge: NavBadge }) {
  const cls =
    badge.kind === 'red'
      ? 'bg-warp-red-soft text-warp-red'
      : 'bg-warp-blue-soft text-warp-blue';

  return (
    <span
      className={cn(
        'mono rounded-full px-[5px] py-px text-[9.5px] font-semibold tabular-nums',
        cls,
      )}
    >
      {badge.value}
    </span>
  );
}

export default function WarpTopnav({ items, onMenuClick }: Props) {
  const stats = useDashboardStore((s) => s.stats);
  const { data: info } = useInfo();
  const versionTag = info?.version ? `V${info.version}` : '';

  return (
    <nav className="soft-topnav" aria-label="Primary">
      <div className="soft-topnav-brand">
        {onMenuClick && (
          <button
            type="button"
            onClick={onMenuClick}
            className="lg:hidden -ml-1 mr-1 inline-flex h-9 w-9 items-center justify-center rounded-md text-text-dim hover:bg-paper"
            aria-label="Open navigation"
          >
            <Menu className="h-5 w-5" />
          </button>
        )}
        <NavLink to="/" className="flex items-center gap-3 no-underline">
          <span className="soft-topnav-mark">
            <Zap className="h-[15px] w-[15px]" strokeWidth={2.4} />
          </span>
          <span className="hidden sm:block">
            <span className="block soft-topnav-title">Warp</span>
            {versionTag && (
              <span className="block soft-topnav-meta">{versionTag} · PROD</span>
            )}
          </span>
        </NavLink>
      </div>

      <div className="soft-topnav-items hidden lg:flex">
        {items.map((item) => {
          const Icon = item.icon;
          const badges = item.badges?.(stats) ?? [];
          return (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === '/'}
              className={({ isActive }) =>
                cn('soft-topnav-item', isActive && 'is-active')
              }
            >
              <Icon className="icon h-[14px] w-[14px]" />
              <span>{item.label}</span>
              {badges.length > 0 && (
                <span className="flex items-center gap-1">
                  {badges.map((b, i) => (
                    <Badge key={i} badge={b} />
                  ))}
                </span>
              )}
            </NavLink>
          );
        })}
      </div>
    </nav>
  );
}
