import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
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
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Hint } from '@/components/ui/tooltip';
import { LoadingState, ErrorState } from '@/components/PageState';
import { PageHeading } from '@/components/PageHeading';
import * as api from '@/api';
import { parseFilterChip } from '@/lib/metricPattern';
import { queryKeys } from '@/lib/queryClient';
import { decodeUrlSafeId } from '@/lib/urlSafeId';
import { MetricKind } from '@/types/metrics';
import type { MetricInstrument } from '@/types/metrics';
import {
  DEFAULT_CONTROLS,
  PERCENTILES,
  RANGES,
  axisTitles,
  breakdownValues,
  buildDatasets,
  buildNumeratorQuery,
  buildRatioDatasets,
  buildSeriesQuery,
  chipToText,
  collapseTooltip,
  formatChange,
  formatNumber,
  headlineSummary,
  isRatioActive,
  kindChartNote,
  kindLabel,
  percentileLabel,
  removeChip,
  resolutionSummary,
  seriesRefetchMs,
  supportsPercentile,
  supportsRatio,
  toggleNumerator,
  unitLabel,
  upsertChip,
  type DatasetSpec,
  type HeadlineSummary,
  type MetricControls,
} from './metricModel';

Chart.register(LineController, LineElement, PointElement, LinearScale, TimeScale, Tooltip, Legend);

const PALETTE = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#06b6d4', '#a855f7', '#ec4899', '#84cc16', '#14b8a6', '#f97316'];
const REST_COLOR = '#9ca3af';

const selectClass = 'h-8 rounded-md border border-border bg-background px-2 text-sm';
const inputClass = 'h-8 rounded-md border border-border bg-background px-2 text-sm';
const toggleClass = (on: boolean) =>
  `h-8 rounded-md border px-3 text-sm ${on ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:bg-muted'}`;

function colorFor(spec: DatasetSpec): string {
  return spec.colorIndex < 0 ? REST_COLOR : PALETTE[spec.colorIndex % PALETTE.length];
}

function tryDecode(id: string | undefined): string | null {
  if (!id) {
    return null;
  }

  try {
    return decodeUrlSafeId(id);
  } catch {
    return null;
  }
}

export default function MetricDetailPage() {
  const { id } = useParams<{ id: string }>();
  const name = tryDecode(id);
  const [controls, setControls] = useState<MetricControls>(DEFAULT_CONTROLS);
  const [chipDraft, setChipDraft] = useState({ key: '', value: '' });
  const [patternDraft, setPatternDraft] = useState('');
  const set = (patch: Partial<MetricControls>) => setControls((c) => ({ ...c, ...patch }));

  const instrumentsQuery = useQuery({
    queryKey: queryKeys.metrics,
    queryFn: () => api.getMetrics(),
    enabled: name !== null,
    // The registry changes when an instrument first appears; this page only needs the one it charts.
    staleTime: 5 * 60_000,
  });
  const instrument: MetricInstrument | undefined = useMemo(
    () => (Array.isArray(instrumentsQuery.data) ? instrumentsQuery.data.find((x) => x.name === name) : undefined),
    [instrumentsQuery.data, name],
  );

  const applicationsQuery = useQuery({
    queryKey: queryKeys.applications,
    queryFn: () => api.getApplications(),
    staleTime: 60_000,
  });
  const applications = useMemo(
    () => (Array.isArray(applicationsQuery.data) ? applicationsQuery.data.map((x) => x.name) : []),
    [applicationsQuery.data],
  );

  const kind = instrument?.kind ?? null;

  const seriesQuery = useQuery({
    queryKey: ['metrics', 'series', name, controls.range, controls.application, controls.breakdown, controls.filters,
      controls.compareDay, controls.compareWeek, controls.view === 'values' ? controls.percentile : null, kind] as const,
    queryFn: () => api.getMetricSeries(name as string, buildSeriesQuery(controls, kind, Date.now())),
    enabled: name !== null && instrument !== undefined,
    refetchInterval: seriesRefetchMs(controls.range),
    placeholderData: keepPreviousData,
  });
  const series = seriesQuery.data;

  const ratioActive = kind !== null && isRatioActive(controls, kind);

  // The ratio numerator is its own request (current filters + breakdown:<pattern>, no breakdown), so values the
  // breakdown read folded into (rest) still count. The denominator is the primary breakdown response's total.
  const numeratorQuery = useQuery({
    queryKey: ['metrics', 'series', 'numerator', name, controls.range, controls.application, controls.breakdown, controls.filters,
      controls.compareDay, controls.compareWeek, controls.numerator, kind] as const,
    queryFn: () => api.getMetricSeries(name as string, buildNumeratorQuery(controls, kind, Date.now()) as string),
    enabled: name !== null && instrument !== undefined && ratioActive && controls.numerator !== null,
    refetchInterval: seriesRefetchMs(controls.range),
    placeholderData: keepPreviousData,
  });
  const numeratorSeries = numeratorQuery.data;

  const datasets = useMemo(() => {
    if (!series) {
      return [];
    }

    return ratioActive
      ? buildRatioDatasets(series, numeratorSeries, controls.numerator)
      : buildDatasets(series, { percentile: controls.percentile, showMinMax: controls.showMinMax });
  }, [series, numeratorSeries, ratioActive, controls.numerator, controls.percentile, controls.showMinMax]);
  const values = useMemo(() => breakdownValues(series), [series]);
  const headline = useMemo(() => (series ? headlineSummary(series) : null), [series]);
  const axes = useMemo(
    () => (kind === null ? { y: '', y1: '' } : axisTitles(kind, instrument?.unit, ratioActive)),
    [kind, instrument?.unit, ratioActive],
  );

  if (name === null) {
    return <ErrorState message="Unknown metric" />;
  }

  if (instrumentsQuery.isError) {
    return <ErrorState message="Unable to load metrics" />;
  }

  if (instrumentsQuery.isLoading) {
    return <LoadingState />;
  }

  if (!instrument) {
    return (
      <div>
        <Link to="/metrics" className="text-sm text-primary hover:underline">← Metrics</Link>
        <ErrorState message="Metric not found" />
      </div>
    );
  }

  const unit = unitLabel(instrument.unit);
  const kindNote = kindChartNote(instrument.kind);
  const addChip = () => {
    const chip = parseFilterChip(`${chipDraft.key}=${chipDraft.value}`);
    if (!chip) {
      return;
    }

    set({ filters: upsertChip(controls.filters, chip) });
    setChipDraft({ key: chipDraft.key, value: '' });
  };

  return (
    <div>
      <Link to="/metrics" className="text-sm text-primary hover:underline">← Metrics</Link>
      <PageHeading className="mb-1 mt-2">
        <span className="font-mono">{instrument.name}</span>
      </PageHeading>
      <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground mb-4">
        <Hint text={kindNote ?? undefined}>
          <span className={kindNote ? 'underline decoration-dotted underline-offset-2' : undefined}>{kindLabel(instrument.kind)}</span>
        </Hint>
        {unit && <span>· {unit}</span>}
        <span>· {instrument.meter}</span>
        {instrument.collapsedAt && (
          <Hint text={collapseTooltip(instrument)}>
            <Badge variant="destructive">collapsed</Badge>
          </Hint>
        )}
        {instrument.description && <span>· {instrument.description}</span>}
      </div>

      <Card className="mb-4">
        <CardContent className="flex flex-wrap items-end gap-x-6 gap-y-3 pt-4">
          <Control label="Range">
            <div className="flex gap-1">
              {RANGES.map((r) => (
                <button key={r.id} type="button" className={toggleClass(controls.range === r.id)} onClick={() => set({ range: r.id })}>
                  {r.label}
                </button>
              ))}
            </div>
          </Control>

          <Control label="Application">
            <select
              className={selectClass}
              value={controls.application ?? ''}
              onChange={(e) => set({ application: e.target.value || null })}
            >
              <option value="">All applications</option>
              {applications.map((x) => <option key={x} value={x}>{x}</option>)}
            </select>
            {applicationsQuery.isError && (
              <p className="mt-1 text-xs text-destructive">Unable to load the application list.</p>
            )}
          </Control>

          <Control label="Break down by">
            <select
              className={selectClass}
              value={controls.breakdown ?? ''}
              onChange={(e) => set({ breakdown: e.target.value || null, numerator: null, view: e.target.value ? controls.view : 'values' })}
            >
              <option value="">None (total)</option>
              {instrument.tagKeys.map((x) => <option key={x} value={x}>{x}</option>)}
            </select>
          </Control>

          <Control label="Compare">
            <div className="flex gap-1">
              <button type="button" className={toggleClass(controls.compareDay)} onClick={() => set({ compareDay: !controls.compareDay })}>1d ago</button>
              <button type="button" className={toggleClass(controls.compareWeek)} onClick={() => set({ compareWeek: !controls.compareWeek })}>7d ago</button>
            </div>
          </Control>

          {supportsPercentile(instrument.kind) && !ratioActive && (
            <Control label="Percentile">
              <select
                className={selectClass}
                value={controls.percentile ?? ''}
                onChange={(e) => set({ percentile: e.target.value ? Number(e.target.value) : null })}
              >
                <option value="">None</option>
                {PERCENTILES.map((p) => <option key={p} value={p}>{percentileLabel(p)}</option>)}
              </select>
            </Control>
          )}

          {instrument.kind === MetricKind.Gauge && (
            <Control label="Range lines">
              <button type="button" className={toggleClass(controls.showMinMax)} onClick={() => set({ showMinMax: !controls.showMinMax })}>
                min / max
              </button>
            </Control>
          )}

          {supportsRatio(instrument.kind) && (
            <Control label="View">
              <div className="flex gap-1">
                <button type="button" className={toggleClass(controls.view === 'values')} onClick={() => set({ view: 'values' })}>Values</button>
                <button
                  type="button"
                  className={toggleClass(controls.view === 'ratio')}
                  disabled={controls.breakdown === null}
                  title={controls.breakdown === null ? 'Pick a tag to break down by first' : undefined}
                  onClick={() => set({ view: 'ratio' })}
                >
                  Ratio
                </button>
              </div>
            </Control>
          )}
        </CardContent>

        <CardContent className="flex flex-wrap items-center gap-2 border-t border-border pt-3">
          <span className="text-xs uppercase tracking-wide text-muted-foreground">Filters</span>
          {controls.filters.map((chip) => (
            <span key={chip.key} className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 font-mono text-xs">
              {chipToText(chip)}
              <button
                type="button"
                aria-label={`Remove filter ${chip.key}`}
                className="text-muted-foreground hover:text-foreground"
                onClick={() => set({ filters: removeChip(controls.filters, chip.key) })}
              >
                ×
              </button>
            </span>
          ))}
          {instrument.tagKeys.length > 0 ? (
            <form
              className="flex items-center gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                addChip();
              }}
            >
              <select
                className={selectClass}
                value={chipDraft.key}
                aria-label="Filter tag"
                onChange={(e) => setChipDraft({ ...chipDraft, key: e.target.value })}
              >
                <option value="">tag…</option>
                {instrument.tagKeys.map((x) => <option key={x} value={x}>{x}</option>)}
              </select>
              <span className="text-muted-foreground">=</span>
              <input
                className={`${inputClass} w-32`}
                placeholder="value or prefix*"
                aria-label="Filter value"
                value={chipDraft.value}
                onChange={(e) => setChipDraft({ ...chipDraft, value: e.target.value })}
              />
              <button type="submit" className={toggleClass(false)} disabled={!chipDraft.key || !chipDraft.value.trim()}>Add</button>
            </form>
          ) : (
            <span className="text-xs text-muted-foreground">This instrument has no tags to filter on.</span>
          )}
        </CardContent>

        {ratioActive && (
          <CardContent className="flex flex-wrap items-center gap-2 border-t border-border pt-3">
            <span className="text-xs uppercase tracking-wide text-muted-foreground">Numerator</span>
            {values.map((v) => (
              <button key={v} type="button" className={`${toggleClass(controls.numerator === v)} font-mono !px-2 !text-xs`} onClick={() => set({ numerator: toggleNumerator(controls.numerator, v) })}>
                {v}
              </button>
            ))}
            {controls.numerator !== null && !values.includes(controls.numerator) && (
              <button type="button" className={`${toggleClass(true)} font-mono !px-2 !text-xs`} onClick={() => set({ numerator: null })}>
                {controls.numerator} ×
              </button>
            )}
            <form
              className="flex items-center gap-1"
              onSubmit={(e) => {
                e.preventDefault();
                const pattern = patternDraft.trim();
                if (pattern) {
                  set({ numerator: pattern });
                }

                setPatternDraft('');
              }}
            >
              <input
                className={`${inputClass} w-28`}
                placeholder="pattern e.g. 5*"
                aria-label="Numerator pattern"
                value={patternDraft}
                onChange={(e) => setPatternDraft(e.target.value)}
              />
              <button type="submit" className={toggleClass(false)} disabled={!patternDraft.trim()}>Add</button>
            </form>
          </CardContent>
        )}
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex flex-wrap items-baseline justify-between gap-2 text-base">
            <span>{ratioActive ? 'Share of total' : 'Over time'}</span>
            <span className="flex items-baseline gap-3">
              {(seriesQuery.isPlaceholderData || (ratioActive && numeratorQuery.isPlaceholderData)) && (
                <span className="text-xs font-normal text-muted-foreground" aria-live="polite">updating…</span>
              )}
              {series && <span className="text-xs font-normal text-muted-foreground">Resolution — {resolutionSummary(series)}</span>}
            </span>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {headline && !ratioActive && !seriesQuery.isError && <HeadlineStrip headline={headline} unit={unit} />}
          {seriesQuery.isError ? (
            <p className="py-8 text-center text-sm text-destructive">Unable to load the series.</p>
          ) : seriesQuery.isLoading || !series ? (
            <LoadingState />
          ) : ratioActive && controls.numerator === null ? (
            <p className="py-8 text-center text-sm text-muted-foreground">Pick a value or a prefix* pattern above to use as the numerator.</p>
          ) : ratioActive && numeratorQuery.isError ? (
            <p className="py-8 text-center text-sm text-destructive">Unable to load the numerator series.</p>
          ) : ratioActive && !numeratorSeries ? (
            <LoadingState />
          ) : (
            <MetricChart
              datasets={datasets}
              axes={axes}
              ratio={ratioActive}
            />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Control({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="mb-1 text-xs uppercase tracking-wide text-muted-foreground">{label}</div>
      {children}
    </div>
  );
}

function HeadlineStrip({ headline, unit }: { headline: HeadlineSummary; unit: string }) {
  const show = (value: number | null) => (value === null ? '—' : `${formatNumber(value)}${unit ? ` ${unit}` : ''}`);

  return (
    <div className="mb-3 flex flex-wrap items-baseline gap-x-6 gap-y-1 text-sm">
      <div>
        <span className="text-xs uppercase tracking-wide text-muted-foreground">{headline.label}</span>{' '}
        <span className="text-lg font-semibold tabular-nums">{show(headline.value)}</span>
      </div>
      {headline.comparisons.map((x) => (
        <div key={x.compare} className="text-muted-foreground">
          vs {x.compare} ago <span className="tabular-nums text-foreground">{show(x.value)}</span>{' '}
          <span className="tabular-nums">({formatChange(x.changePercent)})</span>
        </div>
      ))}
      {headline.excludesIncomplete && (
        <span className="text-xs text-muted-foreground">Excludes the newest bucket, which is still filling (drawn faded).</span>
      )}
    </div>
  );
}

function MetricChart({
  datasets,
  axes,
  ratio,
  height = 320,
}: {
  datasets: DatasetSpec[];
  axes: { y: string; y1: string };
  ratio: boolean;
  height?: number;
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
            grid: { color: gridColor },
            ticks: { color: textColor, font: { size: 10 }, maxRotation: 0, autoSkip: true },
          },
          y: {
            beginAtZero: true,
            position: 'left',
            title: { display: true, text: axes.y, color: textColor, font: { size: 10 } },
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
            title: { display: true, text: axes.y1, color: textColor, font: { size: 10 } },
            ticks: { color: textColor, font: { size: 10 } },
            grid: { drawOnChartArea: false },
          },
        },
        plugins: {
          legend: { labels: { color: textColor, boxWidth: 12, font: { size: 11 } } },
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
  }, [datasets, axes, ratio, hasData]);

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
