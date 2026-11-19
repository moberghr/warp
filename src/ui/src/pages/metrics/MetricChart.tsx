// The metric page's chart (Chart.js line chart over time), shared by the metric detail page and the board's chart tiles.
// Compact mode (a board tile) hides the legend and draws smaller; the datasets, markers and bands are the same.
import { useEffect, useRef } from 'react';
import {
  Chart,
  LineController,
  LineElement,
  PointElement,
  LinearScale,
  TimeScale,
  Tooltip,
  Legend,
  type ScriptableContext,
  type ScriptableLineSegmentContext,
} from 'chart.js';
import 'chartjs-adapter-luxon';
import { annotationBandPlugin, deployMarkerPlugin } from '@/lib/deployMarkerPlugin';
import type { AnnotationBand } from './annotationModel';
import type { DatasetSpec, DeployMarker } from './metricModel';

Chart.register(LineController, LineElement, PointElement, LinearScale, TimeScale, Tooltip, Legend);

const PALETTE = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#06b6d4', '#a855f7', '#ec4899', '#84cc16', '#14b8a6', '#f97316'];
const REST_COLOR = '#9ca3af';

function colorFor(spec: DatasetSpec): string {
  return spec.colorIndex < 0 ? REST_COLOR : PALETTE[spec.colorIndex % PALETTE.length];
}

export function MetricChart({
  datasets,
  axes,
  ratio,
  markers,
  bands,
  bounds,
  height = 320,
  compact = false,
}: {
  datasets: DatasetSpec[];
  axes: { y: string; y1: string };
  ratio: boolean;
  markers: DeployMarker[];
  /** Annotation bands (#333), already clipped to the window. */
  bands: AnnotationBand[];
  /** The window's ends, so the axis spans the selected range and a deploy at its start is on the chart. */
  bounds: { min: number; max: number };
  height?: number;
  /** A board tile: no legend (the lines are named in the tooltip), smaller axis titles. */
  compact?: boolean;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const hasData = datasets.some((d) => d.data.some((p) => p.y !== null));

  useEffect(() => {
    if (!canvasRef.current || !hasData) {
      return;
    }

    const isDark = document.documentElement.classList.contains('dark');
    const gridColor = isDark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.08)';
    const textColor = isDark ? '#888' : '#666';
    const usesY1 = datasets.some((d) => d.axis === 'y1');

    const chart = new Chart(canvasRef.current, {
      type: 'line',
      plugins: [annotationBandPlugin(bands), deployMarkerPlugin(markers)],
      data: {
        datasets: datasets.map((d) => {
          const color = colorFor(d);
          const faint = d.role === 'min' || d.role === 'max';
          // The still-filling bucket reads low: fade and dash the segment into it, and mark the point itself.
          const filling = (index: number) => d.data[index]?.incomplete === true;

          return {
            label: d.label,
            data: d.data,
            yAxisID: d.axis,
            borderColor: d.dashed ? `${color}aa` : color,
            backgroundColor: color,
            borderWidth: faint ? 1 : 2,
            borderDash: d.dashed ? [6, 4] : d.role === 'percentile' ? [2, 3] : faint ? [1, 3] : [],
            pointRadius: (ctx: ScriptableContext<'line'>) => (filling(ctx.dataIndex) ? 3 : 0),
            pointBackgroundColor: `${color}55`,
            pointHoverRadius: 3,
            tension: 0,
            spanGaps: false,
            segment: {
              borderColor: (ctx: ScriptableLineSegmentContext) => (filling(ctx.p1DataIndex) ? `${color}55` : undefined),
              borderDash: (ctx: ScriptableLineSegmentContext) => (filling(ctx.p1DataIndex) ? [3, 3] : undefined),
            },
          };
        }),
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { mode: 'nearest', axis: 'x', intersect: false },
        scales: {
          x: {
            type: 'time',
            min: bounds.min,
            max: bounds.max,
            grid: { color: gridColor },
            ticks: { color: textColor, font: { size: 10 }, maxRotation: 0, autoSkip: true },
          },
          y: {
            beginAtZero: true,
            position: 'left',
            title: { display: !compact, text: axes.y, color: textColor, font: { size: 10 } },
            ticks: {
              color: textColor,
              font: { size: 10 },
              callback: (v) => (ratio ? `${v}%` : v),
            },
            grid: { color: gridColor },
          },
          y1: {
            display: usesY1,
            beginAtZero: true,
            position: 'right',
            title: { display: !compact, text: axes.y1, color: textColor, font: { size: 10 } },
            ticks: { color: textColor, font: { size: 10 } },
            grid: { drawOnChartArea: false },
          },
        },
        plugins: {
          legend: { display: !compact, labels: { color: textColor, boxWidth: 12, font: { size: 11 } } },
          tooltip: {
            callbacks: {
              label: (item) => {
                const base = `${item.dataset.label ?? ''}: ${item.formattedValue}${ratio ? '%' : ''}`;

                return datasets[item.datasetIndex]?.data[item.dataIndex]?.incomplete ? `${base} (still filling)` : base;
              },
            },
          },
        },
      },
    });

    return () => chart.destroy();
  }, [datasets, axes, ratio, hasData, markers, bands, bounds.min, bounds.max, compact]);

  if (!hasData) {
    return (
      <div className="flex items-center justify-center text-sm text-muted-foreground" style={{ height }}>
        No measurements in this range.
      </div>
    );
  }

  return (
    <div style={{ height }}>
      <canvas ref={canvasRef} />
    </div>
  );
}
