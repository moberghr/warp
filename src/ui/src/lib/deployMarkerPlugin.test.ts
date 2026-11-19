import { describe, expect, it } from 'vitest';
import type { AnnotationBand } from '@/pages/metrics/annotationModel';
import {
  ACKNOWLEDGED_NOISE_COLOR,
  ANNOTATION_COLOR,
  annotationBandPlugin,
  bandGeometry,
  deployMarkerPlugin,
  markerGeometry,
  type PlotArea,
} from './deployMarkerPlugin';

// A 1 px = 1 ms axis whose plot area spans times 100..500, so pixel = time.
const area: PlotArea = { left: 100, right: 500, top: 20, bottom: 220 };
const toPixel = (time: number) => time;
const measure = (text: string) => text.length * 6;

function band(start: number, end: number, patch: Partial<AnnotationBand> = {}): AnnotationBand {
  return { id: 1, start, end, ongoing: false, acknowledged: false, label: 'note', ...patch };
}

describe('markerGeometry', () => {
  it('draws a full-height line at each deploy inside the plot area, edges included', () => {
    const lines = markerGeometry([{ time: 100, label: 'a' }, { time: 300, label: 'b' }, { time: 500, label: 'c' }], toPixel, area, false, measure);

    expect(lines).toEqual([
      { x: 100, top: 20, bottom: 220, label: null },
      { x: 300, top: 20, bottom: 220, label: null },
      { x: 500, top: 20, bottom: 220, label: null },
    ]);
  });

  it('skips a deploy left of, right of, or off the axis', () => {
    const lines = markerGeometry(
      [{ time: 99, label: 'before' }, { time: 501, label: 'after' }, { time: Number.NaN, label: 'nowhere' }],
      toPixel,
      area,
      true,
      measure,
    );

    expect(lines).toEqual([]);
  });

  it('puts a label 3 px right of its line on three staggered rows, keyed by list position', () => {
    const lines = markerGeometry(
      [{ time: 50, label: 'skipped' }, { time: 200, label: '1.1' }, { time: 250, label: '1.2' }, { time: 300, label: '1.3' }],
      toPixel,
      area,
      true,
      measure,
    );

    expect(lines.map((x) => x.label)).toEqual([
      { text: '1.1', x: 203, y: 41 }, // index 1 → second row
      { text: '1.2', x: 253, y: 52 }, // index 2 → third row
      { text: '1.3', x: 303, y: 30 }, // index 3 → back to the first row
    ]);
  });

  it('pulls a label left so it ends at the right edge', () => {
    const [line] = markerGeometry([{ time: 490, label: '2.0.0-rc1' }], toPixel, area, true, measure);

    expect(line.label).toEqual({ text: '2.0.0-rc1', x: 500 - 9 * 6, y: 30 });
  });
});

describe('bandGeometry', () => {
  it('shades the band over the full plot height, coloured by acknowledged noise', () => {
    expect(bandGeometry([band(200, 300), band(320, 400, { acknowledged: true })], toPixel, area, false)).toEqual([
      { left: 200, top: 20, width: 100, height: 200, color: ANNOTATION_COLOR, label: null },
      { left: 320, top: 20, width: 80, height: 200, color: ACKNOWLEDGED_NOISE_COLOR, label: null },
    ]);
  });

  it('clips a band to the plot area and skips one with nothing left inside it', () => {
    const rects = bandGeometry([band(0, 150), band(450, 900), band(0, 90), band(600, 700), band(300, 300), band(Number.NaN, 400)], toPixel, area, false);

    expect(rects.map((x) => [x.left, x.width])).toEqual([[100, 50], [450, 50]]);
  });

  it('labels a band at its foot on staggered rows, never narrower than 20 px', () => {
    const rects = bandGeometry([band(200, 400, { label: 'wide' }), band(300, 310, { label: 'narrow' }), band(320, 330, { label: 'third' }), band(340, 360, { label: 'fourth' })], toPixel, area, true);

    expect(rects.map((x) => x.label)).toEqual([
      { text: 'wide', x: 203, y: 216, maxWidth: 194 },
      { text: 'narrow', x: 303, y: 205, maxWidth: 20 },
      { text: 'third', x: 323, y: 194, maxWidth: 20 },
      { text: 'fourth', x: 343, y: 216, maxWidth: 20 },
    ]);
  });
});

// The plugins only paint the geometry; a recording context checks they paint what it says, and nothing without an axis.
function recordingChart(withAxis = true) {
  const calls: string[] = [];
  const ctx = {
    save: () => calls.push('save'),
    restore: () => calls.push('restore'),
    beginPath: () => {},
    stroke: () => calls.push('stroke'),
    setLineDash: () => {},
    moveTo: (x: number, y: number) => calls.push(`moveTo ${x},${y}`),
    lineTo: (x: number, y: number) => calls.push(`lineTo ${x},${y}`),
    fillRect: (x: number, y: number, w: number, h: number) => calls.push(`fillRect ${x},${y},${w},${h}`),
    fillText: (text: string, x: number, y: number) => calls.push(`fillText ${text}@${x},${y}`),
    measureText: (text: string) => ({ width: measure(text) }),
  };
  const chart = { ctx, chartArea: area, scales: withAxis ? { x: { getPixelForValue: toPixel } } : {} };

  return { calls, chart };
}

type Hook = (chart: unknown, args: unknown, options: unknown) => void;

describe('the plugins', () => {
  it('paint each deploy line and its label', () => {
    const { calls, chart } = recordingChart();
    (deployMarkerPlugin([{ time: 200, label: 'v2' }]).afterDatasetsDraw as Hook)(chart, {}, {});

    expect(calls).toEqual(['save', 'moveTo 200,20', 'lineTo 200,220', 'stroke', 'fillText v2@203,30', 'restore']);
  });

  it('paint each band and its edge', () => {
    const { calls, chart } = recordingChart();
    (annotationBandPlugin([band(200, 300)], false).beforeDatasetsDraw as Hook)(chart, {}, {});

    expect(calls).toEqual(['save', 'fillRect 200,20,100,200', 'moveTo 200,20', 'lineTo 200,220', 'stroke', 'restore']);
  });

  it('paint nothing without an x axis or without anything to draw', () => {
    const noAxis = recordingChart(false);
    (deployMarkerPlugin([{ time: 200, label: 'v2' }]).afterDatasetsDraw as Hook)(noAxis.chart, {}, {});
    (annotationBandPlugin([band(200, 300)]).beforeDatasetsDraw as Hook)(noAxis.chart, {}, {});
    const empty = recordingChart();
    (deployMarkerPlugin([]).afterDatasetsDraw as Hook)(empty.chart, {}, {});
    (annotationBandPlugin([]).beforeDatasetsDraw as Hook)(empty.chart, {}, {});

    expect([...noAxis.calls, ...empty.calls]).toEqual([]);
  });
});
