import { type ReactNode } from 'react';
import { Menu } from 'lucide-react';
import { cn } from '@/lib/utils';

interface Props {
  title: string;
  subtitle?: string;
  right?: ReactNode;
  onMenuClick?: () => void;
}

export default function WarpTopbar({ title, subtitle, right, onMenuClick }: Props) {
  return (
    <div
      className={cn(
        'h-[60px] px-6 bg-background flex items-center gap-4 relative shrink-0',
        'after:absolute after:left-6 after:right-6 after:bottom-0 after:h-px',
        'after:bg-gradient-to-r after:from-transparent after:via-warp-green/30 after:to-transparent',
      )}
    >
      {onMenuClick && (
        <button
          type="button"
          onClick={onMenuClick}
          className="lg:hidden -ml-2 p-2 rounded-md text-text-dim hover:text-foreground hover:bg-panel-2"
          aria-label="Open navigation"
        >
          <Menu className="w-5 h-5" />
        </button>
      )}

      <div className="min-w-0 flex-1 lg:flex-initial">
        <div className="font-display text-[16px] font-semibold tracking-tight leading-none truncate">
          {title}
        </div>
        {subtitle && (
          <div className="hidden lg:block text-[11px] text-text-mute mt-1 truncate">
            {subtitle}
          </div>
        )}
      </div>

      {right && (
        <div className="ml-auto flex items-center gap-3.5 text-text-dim text-xs">
          {right}
        </div>
      )}
    </div>
  );
}
