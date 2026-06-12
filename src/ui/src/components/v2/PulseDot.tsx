import { cn } from '@/lib/utils';

type PulseDotProps = {
  className?: string;
  /** Tailwind text color class for the dot (defaults to text-warp-green). */
  colorClass?: string;
  /** Pixel size of the inner dot. The halo is animated to grow ~2.6x. */
  size?: number;
  'aria-label'?: string;
};

export function PulseDot({ className, colorClass = 'text-warp-green', size = 6, 'aria-label': ariaLabel }: PulseDotProps) {
  return (
    <span
      className={cn('relative inline-flex items-center justify-center', colorClass, className)}
      style={{ width: size, height: size }}
      role={ariaLabel ? 'img' : undefined}
      aria-label={ariaLabel}
      aria-hidden={ariaLabel ? undefined : true}
    >
      <span
        className="warp-pulse-halo absolute inset-0 rounded-full"
        style={{ background: 'currentColor', opacity: 0.35 }}
      />
      <span
        className="relative rounded-full"
        style={{ width: size, height: size, background: 'currentColor' }}
      />
    </span>
  );
}
