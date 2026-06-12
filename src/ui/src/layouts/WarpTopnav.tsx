import { memo } from 'react';
import { NavLink } from 'react-router-dom';
import { Zap, Menu, Sun, Moon } from 'lucide-react';
import { useDashboardStore } from '@/stores/dashboard';
import { useInfo } from '@/api/hooks/useInfo';
import { useTheme } from '@/hooks/useTheme';
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

export default memo(WarpTopnav);

function WarpTopnav({ items, onMenuClick }: Props) {
  const stats = useDashboardStore((s) => s.stats);
  const { data: info } = useInfo();
  const versionTag = info?.version ? `V${info.version}` : '';
  const { theme, toggle: toggleTheme } = useTheme();

  return (
    <nav className="soft-topnav" aria-label="Primary">
      <div className="soft-topnav-brand">
        {onMenuClick && (
          <button
            type="button"
            onClick={onMenuClick}
            className="xl:hidden -ml-1 mr-1 inline-flex h-9 w-9 items-center justify-center rounded-md text-text-dim hover:bg-paper"
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

      <div className="soft-topnav-items hidden xl:flex">
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

      <div className="ml-auto flex items-center gap-2 pl-3 border-l border-hair">
        <button
          type="button"
          onClick={toggleTheme}
          className="inline-flex h-9 w-9 items-center justify-center rounded-md text-text-dim hover:bg-paper hover:text-foreground transition-colors"
          aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
          title={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        >
          {theme === 'dark' ? (
            <Sun className="h-4 w-4" />
          ) : (
            <Moon className="h-4 w-4" />
          )}
        </button>
      </div>
    </nav>
  );
}
