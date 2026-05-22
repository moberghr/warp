import * as React from 'react';
import { ArrowDown, ArrowUp, type LucideIcon } from 'lucide-react';
import { Sparkline } from './Sparkline';
import { Panel } from './Panel';
import { cn } from '@/lib/utils';

type Trend = {
  /** Display value like "12%" or "4". */
  value: string;
  direction: 'up' | 'down';
  /** Optional "good/bad" interpretation. Defaults: up = good, down = good for "failed" inverts via `inverse`. */
  inverse?: boolean;
};

type StatCardProps = {
  label: string;
  value: React.ReactNode;
  /** Lucide icon rendered next to the label. */
  icon: LucideIcon;
  /**
   * Tailwind text color class for the accent (e.g. `text-warp-red`). Drives the
   * big number color, icon color, and the left accent stripe. Defaults to the
   * default foreground.
   */
  accentClass?: string;
  /** Raw color used for the accent stripe & sparkline fill. */
  accentColor?: string;
  sparkValues?: number[];
  trend?: Trend;
  sub?: React.ReactNode;
  /** Whole card click target (e.g. drill-through link). */
  href?: string;
  /** Render-as override (e.g. RR Link). Default `'a'` if `href` is set. */
  as?: React.ElementType;
  className?: string;
};

/**
 * V2 Command stat card: icon + label, oversized mono value, optional trend chip,
 * optional sparkline, and a sub-line. Optional left accent stripe.
 */
export function StatCard({
  label,
  value,
  icon: Icon,
  accentClass,
  accentColor,
  sparkValues,
  trend,
  sub,
  href,
  as,
  className,
}: StatCardProps) {
  const trendUp = trend?.direction === 'up';
  const trendGood = trend ? trendUp !== Boolean(trend.inverse) : false;
  const Comp = (as ?? (href ? 'a' : 'div')) as React.ElementType;
  const linkProps = href ? (as ? { to: href } : { href }) : {};

  return (
    <Comp
      {...linkProps}
      className={cn(
        'block min-h-[108px] no-underline',
        href && 'transition-colors hover:border-border-hi focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/40',
        className,
      )}
    >
      <Panel
        accent={accentColor}
        className="flex h-full flex-col gap-2 px-4 py-3.5 text-foreground"
      >
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Icon className={cn('h-3.5 w-3.5 opacity-90', accentClass ?? 'text-text-dim')} />
            <span className="text-[11.5px] font-medium text-text-dim">{label}</span>
          </div>
          {trend && (
            <span
              className={cn(
                'mono inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[10.5px] font-semibold',
                trendGood
                  ? 'bg-warp-green-soft text-warp-green'
                  : 'bg-warp-red-soft text-warp-red',
              )}
            >
              {trendUp ? <ArrowUp className="h-3 w-3" /> : <ArrowDown className="h-3 w-3" />}
              {trend.value}
            </span>
          )}
        </div>

        <div className="flex items-end justify-between">
          <span
            className={cn(
              'font-display tabular-nums text-[30px] font-semibold leading-none tracking-[-0.5px]',
              accentClass ?? 'text-foreground',
            )}
          >
            {value}
          </span>
          {sparkValues && (
            <span className={cn(accentClass ?? 'text-text-dim')}>
              <Sparkline
                values={sparkValues}
                fill={accentColor ? `${accentColor}1f` : undefined}
              />
            </span>
          )}
        </div>

        {sub && <div className="mono text-[10.5px] text-text-mute">{sub}</div>}
      </Panel>
    </Comp>
  );
}
