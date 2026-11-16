// The Chart.js plugin that draws deploy markers (#325 deploy history), shared by the metric detail chart (#327) and the
// health-board sparklines (#326). Pure canvas drawing: the markers themselves come from metricModel.deployMarkers.
import type { ChartType, Plugin } from 'chart.js';
import type { DeployMarker } from '@/pages/metrics/metricModel';

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
