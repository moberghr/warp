import { areaPath, linePath } from '@/lib/svgPath';

type SparklineProps = {
  values: number[];
  width?: number;
  height?: number;
  /** CSS color for the stroke. Use `currentColor` and set a Tailwind text-* class on the parent. */
  stroke?: string;
  /** Optional fill (typically the stroke at low alpha). */
  fill?: string;
  /** Y-axis padding inside the box (default 2). */
  padY?: number;
  className?: string;
};

/**
 * Tiny SVG sparkline used inside V2 stat cards. Values are expected on a 0..1 scale;
 * they're scaled internally so the line fills the box vertically. Use `text-*` on
 * the parent and `stroke="currentColor"` (default) to inherit the accent color.
 */
export function Sparkline({
  values,
  width = 70,
  height = 20,
  stroke = 'currentColor',
  fill,
  padY = 2,
  className,
}: SparklineProps) {
  const scaled = values.map((v) => v * 100);
  const d = linePath(scaled, width, height, padY);
  const filled = fill ? areaPath(scaled, width, height, padY) : null;

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      className={className}
      style={{ display: 'block' }}
      aria-hidden="true"
    >
      {filled && <path d={filled} fill={fill} />}
      <path
        d={d}
        fill="none"
        stroke={stroke}
        strokeWidth={1.4}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
