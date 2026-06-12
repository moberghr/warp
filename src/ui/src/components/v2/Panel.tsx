import * as React from 'react';
import { cn } from '@/lib/utils';

type PanelProps = React.HTMLAttributes<HTMLDivElement> & {
  /** Adds a 2px vertical accent stripe on the left edge. Pass any CSS color. */
  accent?: string;
};

/**
 * Surface card used throughout the V2 Command system: inset top-highlight + soft
 * drop shadow + optional left accent stripe.
 */
export function Panel({ className, accent, children, ...props }: PanelProps) {
  return (
    <div className={cn('warp-panel relative overflow-hidden', className)} {...props}>
      {accent && (
        <span
          aria-hidden="true"
          className="pointer-events-none absolute left-0 top-0 bottom-0 w-[2px]"
          style={{
            background: `linear-gradient(180deg, ${accent}, transparent)`,
            opacity: 0.7,
          }}
        />
      )}
      {children}
    </div>
  );
}

type PanelHeaderProps = React.HTMLAttributes<HTMLDivElement> & {
  /** Eyebrow text (uppercase mono). Pass `null` to omit. */
  eyebrow?: React.ReactNode;
  /** Right-side header slot (buttons, counters, etc.). */
  action?: React.ReactNode;
  /** Color for the eyebrow. Defaults to `text-mute`. */
  eyebrowColor?: string;
};

/**
 * Panel header with a subtle gradient bottom, mono eyebrow on the left, and
 * an optional action slot on the right.
 */
export function PanelHeader({
  className,
  eyebrow,
  action,
  eyebrowColor,
  children,
  ...props
}: PanelHeaderProps) {
  return (
    <div
      className={cn(
        'flex items-center justify-between border-b border-border px-4 py-2.5',
        'bg-gradient-to-b from-foreground/[0.012] to-transparent dark:from-white/[0.015]',
        className,
      )}
      {...props}
    >
      <div className="flex min-w-0 items-center gap-2">
        {eyebrow && (
          <span className="warp-eyebrow" style={eyebrowColor ? { color: eyebrowColor } : undefined}>
            {eyebrow}
          </span>
        )}
        {children}
      </div>
      {action && <div className="flex items-center gap-2">{action}</div>}
    </div>
  );
}

type EyebrowProps = React.HTMLAttributes<HTMLSpanElement> & {
  color?: string;
};

export function Eyebrow({ className, color, style, children, ...props }: EyebrowProps) {
  return (
    <span
      className={cn('warp-eyebrow', className)}
      style={{ ...(color ? { color } : null), ...style }}
      {...props}
    >
      {children}
    </span>
  );
}
