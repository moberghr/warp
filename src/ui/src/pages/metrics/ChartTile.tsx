// A chart tile on a metric board (spec 2026-11-19 batch 1): the metric page's chart of one instrument with the tile's
// stored controls over the board's range, fed by the same GET metrics/{id}/series. Wide (two grid columns on desktop),
// compact (no controls, no legend), and lazy: the series is read only while the tile is in view.
import { useMemo } from 'react';
import { Link } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import * as api from '@/api';
import { LoadingState } from '@/components/PageState';
import { useInView } from '@/hooks/useInView';
import type { ApplicationDeployModel } from '@/types/applications';
import type { MetricKind } from '@/types/metrics';
import type { MetricBoardTileResult } from '@/types/metricBoard';
import { MetricChart } from './MetricChart';
import { chartControls, chartTileHref } from './chartTileModel';
import { describeTile } from './boardModel';
import {
  axisTitles,
  buildDatasets,
  buildNumeratorQuery,
  buildRatioDatasets,
  buildSeriesQuery,
  deployMarkers,
  isRatioActive,
  numeratorQueryKey,
  seriesQueryKey,
  type RangeSelection,
} from './metricModel';

/** The chart of `instrument` with the stored `query` over `selection`; reads only while `enabled`. */
export function ChartTileChart({
  instrument,
  kind,
  unit,
  query,
  selection,
  refetchMs,
  enabled,
  deploys,
  height = 200,
}: {
  instrument: string;
  /** The instrument's registry kind; null when it is not in the registry (nothing to read). */
  kind: MetricKind | null;
  unit: string | null;
  query: string | null;
  selection: RangeSelection;
  refetchMs: number | false;
  enabled: boolean;
  deploys: ApplicationDeployModel[] | undefined;
  height?: number;
}) {
  const { controls, rate } = useMemo(() => chartControls(query, selection), [query, selection]);
  const ratioActive = kind !== null && isRatioActive(controls, kind);
  const reading = enabled && kind !== null;

  // The same request and cache key as the metric page, so opening the chart from the tile shows it at once.
  const seriesQuery = useQuery({
    queryKey: seriesQueryKey(instrument, controls, kind),
    queryFn: () => api.getMetricSeries(instrument, buildSeriesQuery(controls, kind, Date.now())),
    enabled: reading,
    refetchInterval: reading ? refetchMs : false,
    placeholderData: keepPreviousData,
  });
  const numeratorQuery = useQuery({
    queryKey: numeratorQueryKey(instrument, controls, kind),
    queryFn: () => api.getMetricSeries(instrument, buildNumeratorQuery(controls, kind, Date.now()) as string),
    enabled: reading && ratioActive && controls.numerator !== null,
    refetchInterval: reading ? refetchMs : false,
    placeholderData: keepPreviousData,
  });
  const series = seriesQuery.data;
  const numeratorSeries = numeratorQuery.data;

  const datasets = useMemo(() => {
    if (!series) {
      return [];
    }

    return ratioActive
      ? buildRatioDatasets(series, numeratorSeries, controls.numerator)
      : buildDatasets(series, { percentile: controls.percentile, showMinMax: controls.showMinMax, rate });
  }, [series, numeratorSeries, ratioActive, controls.numerator, controls.percentile, controls.showMinMax, rate]);
  const markers = useMemo(
    () => (series ? deployMarkers(deploys ?? [], { from: Date.parse(series.from), to: Date.parse(series.to) }, controls.application) : []),
    [deploys, controls.application, series],
  );
  const axes = useMemo(() => (kind === null ? { y: '', y1: '' } : axisTitles(kind, unit, ratioActive, rate)), [kind, unit, ratioActive, rate]);
  const message = (text: string, error = false) => (
    <div className={`flex items-center justify-center text-xs ${error ? 'text-destructive' : 'text-muted-foreground'}`} style={{ height }}>
      {text}
    </div>
  );

  if (kind === null) {
    return message('The instrument is not in the registry yet.');
  }

  if (seriesQuery.isError || (ratioActive && numeratorQuery.isError)) {
    return message('Unable to load the series.', true);
  }

  if (ratioActive && controls.numerator === null) {
    return message('The ratio view has no numerator: edit the tile or open the chart to pick one.');
  }

  if (!series || (ratioActive && !numeratorSeries)) {
    return enabled ? <div style={{ height }}><LoadingState /></div> : message('Loads when in view.');
  }

  return (
    <MetricChart
      datasets={datasets}
      axes={axes}
      ratio={ratioActive}
      markers={markers}
      bands={[]}
      bounds={{ min: Date.parse(series.from), max: Date.parse(series.to) }}
      height={height}
      compact
    />
  );
}

/** A chart tile on the board: title (linking to the metric page with the tile's controls and the board's range) and chart. */
export function ChartTileCard({
  result,
  selection,
  refetchMs,
  deploys,
  onEdit,
  onDelete,
}: {
  result: MetricBoardTileResult;
  selection: RangeSelection;
  refetchMs: number | false;
  deploys: ApplicationDeployModel[] | undefined;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const { tile } = result;
  const [ref, inView] = useInView<HTMLDivElement>();
  const description = describeTile(tile);

  return (
    <div ref={ref} data-testid="chart-tile" className="rounded-lg border-2 border-border bg-card p-3 sm:col-span-2">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <Link to={chartTileHref(tile.instrument, tile.chartQuery, selection)} className="block truncate text-sm font-medium hover:underline" title={description}>
            {tile.name}
          </Link>
          <div className="truncate font-mono text-[11px] text-muted-foreground">{description}</div>
        </div>
        <span className="flex shrink-0 gap-3 text-xs">
          <button type="button" className="text-muted-foreground hover:underline" onClick={onEdit}>Edit</button>
          <button type="button" className="text-red-600 hover:underline" onClick={onDelete}>Delete</button>
        </span>
      </div>
      <div className="mt-2">
        <ChartTileChart
          instrument={tile.instrument}
          kind={result.instrumentKind}
          unit={result.unit}
          query={tile.chartQuery}
          selection={selection}
          refetchMs={refetchMs}
          enabled={inView}
          deploys={deploys}
        />
      </div>
    </div>
  );
}
