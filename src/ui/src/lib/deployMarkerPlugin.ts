// The Chart.js plugins that draw deploy markers (#325 deploy history) and annotation bands (#333), shared by the metric
// detail chart (#327) and the health-board sparklines (#326). The geometry is pure (markerGeometry, bandGeometry) and
// unit-tested; the plugins only paint what it returns. The markers and bands themselves come from
// metricModel.deployMarkers and annotationModel.annotationBands.
import type { ChartType, Plugin } from 'chart.js';
import type { DeployMarker } from '@/pages/metrics/metricModel';
import type { AnnotationBand } from '@/pages/metrics/annotationModel';

export const DEPLOY_COLOR = '#f59e0b';

// Annotation bands (#333): known-issue notes shaded over their range, beside the deploy markers on the same charts.
export const ANNOTATION_COLOR = '#0ea5e9';
export const ACKNOWLEDGED_NOISE_COLOR = '#64748b';

/** The plot area in canvas pixels (Chart.js `chartArea`). */
export interface PlotArea {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/** Labels are staggered over this many rows, so neighbouring labels do not overprint. */
const LABEL_ROWS = 3;
const LABEL_ROW_HEIGHT = 11;

export interface MarkerLine {
  x: number;
  top: number;
  bottom: number;
  label: { text: string; x: number; y: number } | null;
}

/**
 * Where each deploy line goes: a vertical line at its time, skipped when it falls outside the plot area. A label sits
 * 3 px right of the line, pulled left so it never runs past the right edge, on one of three staggered rows chosen by the
 * marker's position in the list (so a skipped marker does not reshuffle the rows of the others).
 */
export function markerGeometry(
  markers: DeployMarker[],
  toPixel: (time: number) => number,
  area: PlotArea,
  labels: boolean,
  measure: (text: string) => number,
): MarkerLine[] {
  return markers.flatMap((marker, index) => {
    const x = toPixel(marker.time);
    if (!Number.isFinite(x) || x < area.left || x > area.right) {
      return [];
    }

    const label = labels
      ? { text: marker.label, x: Math.min(x + 3, area.right - measure(marker.label)), y: area.top + 10 + (index % LABEL_ROWS) * LABEL_ROW_HEIGHT }
      : null;

    return [{ x, top: area.top, bottom: area.bottom, label }];
  });
}

export interface BandRect {
  left: number;
  top: number;
  width: number;
  height: number;
  color: string;
  label: { text: string; x: number; y: number; maxWidth: number } | null;
}

/**
 * Where each annotation band goes: its range clipped to the plot area, skipped when nothing of it is left. A label sits
 * at the band's foot, staggered like the deploy labels from the bottom up, and is never squeezed below 20 px.
 */
export function bandGeometry(bands: AnnotationBand[], toPixel: (time: number) => number, area: PlotArea, labels: boolean): BandRect[] {
  return bands.flatMap((band, index) => {
    const left = Math.max(area.left, toPixel(band.start));
    const right = Math.min(area.right, toPixel(band.end));
    if (!(right > left)) {
      return [];
    }

    const label = labels
      ? { text: band.label, x: left + 3, y: area.bottom - 4 - (index % LABEL_ROWS) * LABEL_ROW_HEIGHT, maxWidth: Math.max(20, right - left - 6) }
      : null;

    return [{
      left,
      top: area.top,
      width: right - left,
      height: area.bottom - area.top,
      color: band.acknowledged ? ACKNOWLEDGED_NOISE_COLOR : ANNOTATION_COLOR,
      label,
    }];
  });
}

/**
 * Draws each deploy as a dashed vertical line. With `labels` the version is written beside it; a sparkline leaves them
 * off (there is no room) and relies on a tooltip.
 */
export function deployMarkerPlugin<T extends ChartType = 'line'>(markers: DeployMarker[], labels = true): Plugin<T> {
  return {
    id: 'warpDeployMarkers',
    afterDatasetsDraw(chart) {
      const { ctx, chartArea, scales } = chart;
      const x = scales.x;
      if (!x || markers.length === 0) {
        return;
      }

      ctx.save();
      ctx.strokeStyle = DEPLOY_COLOR;
      ctx.fillStyle = DEPLOY_COLOR;
      ctx.lineWidth = 1;
      ctx.setLineDash([4, 4]);
      ctx.font = '10px sans-serif';
      for (const line of markerGeometry(markers, (t) => x.getPixelForValue(t), chartArea, labels, (text) => ctx.measureText(text).width)) {
        ctx.beginPath();
        ctx.moveTo(line.x, line.top);
        ctx.lineTo(line.x, line.bottom);
        ctx.stroke();
        if (line.label) {
          ctx.fillText(line.label.text, line.label.x, line.label.y);
        }
      }
      ctx.restore();
    },
  };
}

/**
 * Shades each annotation's range behind the lines: a sky band for a note, a slate band for acknowledged noise, each
 * with a solid left edge so adjacent bands stay apart. With `labels` the start of the note is written at the band's
 * foot; a sparkline leaves it off.
 */
export function annotationBandPlugin<T extends ChartType = 'line'>(bands: AnnotationBand[], labels = true): Plugin<T> {
  return {
    id: 'warpAnnotationBands',
    beforeDatasetsDraw(chart) {
      const { ctx, chartArea, scales } = chart;
      const x = scales.x;
      if (!x || bands.length === 0) {
        return;
      }

      ctx.save();
      ctx.font = '10px sans-serif';
      for (const rect of bandGeometry(bands, (t) => x.getPixelForValue(t), chartArea, labels)) {
        ctx.fillStyle = `${rect.color}22`;
        ctx.fillRect(rect.left, rect.top, rect.width, rect.height);
        ctx.strokeStyle = rect.color;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(rect.left, rect.top);
        ctx.lineTo(rect.left, rect.top + rect.height);
        ctx.stroke();
        if (rect.label) {
          ctx.fillStyle = rect.color;
          ctx.fillText(rect.label.text, rect.label.x, rect.label.y, rect.label.maxWidth);
        }
      }
      ctx.restore();
    },
  };
}
