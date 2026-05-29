import { NavLink, useLocation } from 'react-router-dom';
import { Zap, Settings, LogOut } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useDashboardStore } from '@/stores/dashboard';
import { PulseDot } from '@/components/v2/PulseDot';
import { useInfo } from '@/api/hooks/useInfo';
import * as api from '@/api';
import { config } from '@/config';
import { cn } from '@/lib/utils';
import type { WarpNavItem, NavBadge } from './warpNavItems';

function Badge({ badge, muted }: { badge: NavBadge; muted: boolean }) {
  if (muted) {
    return (
      <span className="mono text-[9.5px] font-semibold text-text-mute tabular-nums">
        {badge.value}
      </span>
    );
  }

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

interface Props {
  items: WarpNavItem[];
  onNavigate?: () => void;
  /** If true, render in mobile-drawer mode (no width constraint, no outer borders). */
  mobile?: boolean;
}

export default function WarpSidebar({ items, onNavigate, mobile = false }: Props) {
  const stats = useDashboardStore((s) => s.stats);
  const location = useLocation();
  const { data: info } = useInfo();
  const versionTag = info?.version ? `v${info.version}` : '';

  const { data: servers } = useQuery({
    queryKey: ['servers'],
    queryFn: () => api.getServers(),
    staleTime: 10_000,
    refetchInterval: 15_000,
  });
  const serverCount = servers?.length ?? 0;
  const totalServers = stats?.servers ?? serverCount;
  const workerCount = servers?.reduce((acc, s) => acc + (s.workers?.length ?? 0), 0) ?? 0;
  const healthy = serverCount > 0;

  return (
    <div
      className={cn(
        'flex flex-col relative',
        mobile
          ? 'w-full'
          : 'hidden lg:flex w-[220px] shrink-0 border-r border-border bg-gradient-to-b from-panel to-background',
      )}
    >
      {!mobile && (
        <div className="flex items-center gap-2.5 px-[18px] pt-[18px] pb-[14px] relative">
          <div className="absolute left-[18px] right-[18px] bottom-0 h-px bg-gradient-to-r from-transparent via-border to-transparent" />
          <div
            className="relative w-7 h-7 rounded-[8px] flex items-center justify-center shadow-[0_2px_10px_rgba(34,197,94,0.35)]"
            style={{
              background: 'linear-gradient(135deg, #22c55e, #16a34a)',
            }}
          >
            <Zap className="w-[15px] h-[15px] text-white" strokeWidth={2.4} />
            <span className="absolute inset-0 rounded-[8px] shadow-[inset_0_1px_0_rgba(255,255,255,0.25)]" />
          </div>
          <div className="flex-1 min-w-0">
            <div className="font-display font-bold text-[15px] leading-none tracking-tight">
              Warp
            </div>
            {versionTag && (
              <div className="mono text-[9.5px] text-text-mute mt-[3px] tracking-wider uppercase">
                {versionTag}
              </div>
            )}
          </div>
        </div>
      )}

      <div className="px-2.5 pt-3.5 pb-2 flex-1 overflow-y-auto">
        <div className="warp-eyebrow px-2 pb-1.5">Workspace</div>
        <nav className="flex flex-col">
          {items.map((it) => {
            const Icon = it.icon;
            const isActive =
              it.to === '/'
                ? location.pathname === '/'
                : location.pathname.startsWith(it.to.split('/').slice(0, 2).join('/'));
            const badges = it.badges ? it.badges(stats) : [];

            return (
              <NavLink
                key={`${it.to}-${it.label}`}
                to={it.to}
                end={it.to === '/'}
                onClick={onNavigate}
                className={cn(
                  'flex items-center gap-2.5 px-2.5 py-2 rounded-lg mb-[1px] text-[13px] font-medium',
                  'border-l-2 transition-colors relative',
                  isActive
                    ? 'border-warp-green bg-panel-2 text-foreground bg-gradient-to-r from-warp-green/[0.10] to-transparent shadow-[inset_0_1px_0_rgba(255,255,255,0.04)]'
                    : 'border-transparent text-text-dim hover:text-foreground hover:bg-panel-2/60',
                )}
              >
                <Icon
                  className={cn(
                    'w-[15px] h-[15px] shrink-0',
                    isActive ? 'text-warp-green' : 'opacity-85',
                  )}
                />
                <span className="flex-1 truncate">{it.label}</span>
                {badges.map((b, i) => (
                  <Badge key={i} badge={b} muted={!isActive} />
                ))}
              </NavLink>
            );
          })}
        </nav>
      </div>

      <div className="mt-auto">
        <div className="px-2.5 pb-2 pt-1">
          <NavLink
            to="/settings"
            onClick={onNavigate}
            className={({ isActive }) =>
              cn(
                'flex items-center gap-2.5 px-2.5 py-2 rounded-lg text-[13px] font-medium',
                'border-l-2 transition-colors',
                isActive
                  ? 'border-warp-green bg-panel-2 text-foreground bg-gradient-to-r from-warp-green/[0.10] to-transparent'
                  : 'border-transparent text-text-dim hover:text-foreground hover:bg-panel-2/60',
              )
            }
          >
            <Settings className="w-[15px] h-[15px] shrink-0 opacity-85" />
            <span className="flex-1 truncate">Settings</span>
          </NavLink>
          {config.hasBuiltInLogin && (
            <button
              type="button"
              onClick={async () => {
                try {
                  await api.logout();
                } finally {
                  window.location.reload();
                }
              }}
              className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg text-[13px] font-medium border-l-2 border-transparent text-text-dim hover:text-foreground hover:bg-panel-2/60 w-full"
            >
              <LogOut className="w-[15px] h-[15px] shrink-0 opacity-85" />
              <span className="flex-1 truncate text-left">Log out</span>
            </button>
          )}
        </div>

      <div className="px-3.5 py-3 border-t border-border bg-gradient-to-b from-transparent to-white/[0.012] dark:to-white/[0.012]">
        <div className="warp-eyebrow mb-2.5">Cluster</div>
        <div className="flex items-center gap-2 mb-2.5">
          <PulseDot colorClass={healthy ? 'text-warp-green' : 'text-warp-amber'} size={5} />
          <span className="text-xs font-medium">{healthy ? 'Healthy' : 'Pending'}</span>
          <span
            className={cn(
              'mono ml-auto rounded px-1.5 py-px text-[10.5px] font-semibold',
              healthy ? 'bg-warp-green-soft text-warp-green' : 'bg-warp-amber-soft text-warp-amber',
            )}
          >
            {serverCount > 0 ? `${serverCount}/${totalServers || serverCount}` : '—/—'}
          </span>
        </div>
        <div className="mono flex justify-between text-[11px] text-text-mute mb-[3px]">
          <span>servers</span>
          <span className="text-foreground">{totalServers}</span>
        </div>
        <div className="mono flex justify-between text-[11px] text-text-mute mb-1.5">
          <span>workers</span>
          <span className="text-foreground">{workerCount > 0 ? workerCount : '—'}</span>
        </div>
        <div className="h-[3px] bg-panel-2 rounded-[2px] overflow-hidden">
          <div
            className="h-full bg-gradient-to-r from-warp-green to-warp-green"
            style={{ width: healthy ? '100%' : '0%' }}
          />
        </div>
      </div>
      </div>
    </div>
  );
}
