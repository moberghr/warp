// The Chart.js plugin that draws deploy markers (#325 deploy history), shared by the metric detail chart (#327) and the
// health-board sparklines (#326). Pure canvas drawing: the markers themselves come from metricModel.deployMarkers.
import type { ChartType, Plugin } from 'chart.js';
import type { DeployMarker } from '@/pages/metrics/metricModel';
import type { AnnotationBand } from '@/pages/metrics/annotationModel';

export const DEPLOY_COLOR = '#f59e0b';

/**
 * Draws each deploy as a dashed vertical line. With `labels` the version is written beside it, staggered so
 * neighbouring labels do not overprint; a sparkline leaves them off (there is no room) and relies on a tooltip.
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
      markers.forEach((marker, index) => {
        const px = x.getPixelForValue(marker.time);
        if (px < chartArea.left || px > chartArea.right) {
          return;
        }

        ctx.beginPath();
        ctx.moveTo(px, chartArea.top);
        ctx.lineTo(px, chartArea.bottom);
        ctx.stroke();
        if (labels) {
          ctx.fillText(marker.label, Math.min(px + 3, chartArea.right - ctx.measureText(marker.label).width), chartArea.top + 10 + (index % 3) * 11);
        }
      });
      ctx.restore();
    },
  };
}

// Annotation bands (#333): known-issue notes shaded over their range, beside the deploy markers on the same charts.
export const ANNOTATION_COLOR = '#0ea5e9';
export const ACKNOWLEDGED_NOISE_COLOR = '#64748b';

/**
 * Shades each annotation's range (already clipped to the window by annotationModel.annotationBands) behind the lines:
 * a sky band for a note, a slate band for acknowledged noise, each with a solid left edge so adjacent bands stay
 * apart. With `labels` the start of the note is written at the band's foot; a sparkline leaves it off.
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
      bands.forEach((band, index) => {
        const left = Math.max(chartArea.left, x.getPixelForValue(band.start));
        const right = Math.min(chartArea.right, x.getPixelForValue(band.end));
        if (right <= left) {
          return;
        }

        const color = band.acknowledged ? ACKNOWLEDGED_NOISE_COLOR : ANNOTATION_COLOR;
        ctx.fillStyle = `${color}22`;
        ctx.fillRect(left, chartArea.top, right - left, chartArea.bottom - chartArea.top);
        ctx.strokeStyle = color;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(left, chartArea.top);
        ctx.lineTo(left, chartArea.bottom);
        ctx.stroke();
        if (labels) {
          ctx.fillStyle = color;
          ctx.fillText(band.label, left + 3, chartArea.bottom - 4 - (index % 3) * 11, Math.max(20, right - left - 6));
        }
      });
      ctx.restore();
    },
  };
}
