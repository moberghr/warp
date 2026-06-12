/**
 * Smoothed bezier line path through a value series, scaled into a w×h box.
 * Min defaults to 0 so the series sits anchored at the bottom of the area.
 */
export function linePath(values: number[], w: number, h: number, padY = 4): string {
  if (!values.length) {
    return '';
  }

  const max = Math.max(...values, 1);
  const sx = (i: number) => (i / (values.length - 1 || 1)) * w;
  const sy = (v: number) => h - padY - (v / max) * (h - padY * 2);

  // Catmull-Rom → cubic Bezier. Produces a curve that actually passes through
  // each sample point with C1 continuity — visually smoother than the previous
  // mid-point quadratic, and crucially does not flatten sharp peaks.
  const pts = values.map((v, i) => ({ x: sx(i), y: sy(v) }));
  let d = `M ${pts[0].x.toFixed(2)} ${pts[0].y.toFixed(2)}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i - 1] ?? pts[i];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[i + 2] ?? p2;
    const cp1x = p1.x + (p2.x - p0.x) / 6;
    const cp1y = p1.y + (p2.y - p0.y) / 6;
    const cp2x = p2.x - (p3.x - p1.x) / 6;
    const cp2y = p2.y - (p3.y - p1.y) / 6;
    d += ` C ${cp1x.toFixed(2)} ${cp1y.toFixed(2)} ${cp2x.toFixed(2)} ${cp2y.toFixed(2)} ${p2.x.toFixed(2)} ${p2.y.toFixed(2)}`;
  }

  return d;
}

/** Same as linePath but closes the shape into a filled area. */
export function areaPath(values: number[], w: number, h: number, padY = 4): string {
  const line = linePath(values, w, h, padY);
  if (!line) {
    return '';
  }

  return `${line} L ${w} ${h} L 0 ${h} Z`;
}
