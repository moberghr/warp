// The tile editor's controls for a chart tile: the metric page's controls (application, breakdown, filters, compare,
// percentile, rate, ratio view, min/max), written back as the tile's stored query, with a live preview of the chart over
// the board's range.
import { useMemo, useState } from 'react';
import { parseFilterChip, parsePattern } from '@/lib/metricPattern';
import { inputClass } from '@/pages/slo/shared';
import { MetricKind, type MetricInstrument } from '@/types/metrics';
import type { ApplicationDeployModel } from '@/types/applications';
import { ChartTileChart } from './ChartTile';
import { chartControls, chartQueryFromControls } from './chartTileModel';
import {
  PERCENTILES,
  RATES,
  chipToText,
  dimensionKeys,
  dimensionLabel,
  percentileLabel,
  removeChip,
  supportsPercentile,
  supportsRate,
  supportsRatio,
  upsertChip,
  type MetricControls,
  type RangeSelection,
  type RateId,
} from './metricModel';

const toggleClass = (on: boolean) =>
  `h-8 rounded-md border px-3 text-sm ${on ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:bg-muted'}`;

export function ChartTileEditor({
  instrument,
  query,
  onChange,
  selection,
  refetchMs,
  deploys,
}: {
  instrument: MetricInstrument | undefined;
  query: string | null;
  onChange: (query: string | null) => void;
  selection: RangeSelection;
  refetchMs: number | false;
  deploys: ApplicationDeployModel[] | undefined;
}) {
  const { controls, rate } = useMemo(() => chartControls(query, selection), [query, selection]);
  const [chipDraft, setChipDraft] = useState({ key: '', value: '' });
  const set = (patch: Partial<MetricControls>, nextRate: RateId = rate) => onChange(chartQueryFromControls({ ...controls, ...patch }, nextRate));
  const kind = instrument?.kind ?? null;
  const keys = dimensionKeys(instrument?.tagKeys ?? []);
  const ratio = controls.view === 'ratio';
  const chip = parseFilterChip(`${chipDraft.key}=${chipDraft.value}`);

  return (
    <div className="space-y-3">
      <Row label="Application (optional)">
        <DraftInput className={inputClass} aria-label="Application" value={controls.application ?? ''} onCommit={(application) => set({ application })} />
      </Row>
      <Row label="Break down by">
        <select
          className={inputClass}
          value={controls.breakdown ?? ''}
          onChange={(e) => set({ breakdown: e.target.value || null, numerator: null, view: e.target.value ? controls.view : 'values' })}
        >
          <option value="">None (total)</option>
          {controls.breakdown && !keys.includes(controls.breakdown) && <option value={controls.breakdown}>{controls.breakdown}</option>}
          {keys.map((x) => <option key={x} value={x}>{dimensionLabel(x)}</option>)}
        </select>
      </Row>
      <Row label="Filters">
        <div className="flex flex-wrap items-center gap-1">
          {controls.filters.map((x) => (
            <span key={x.key} className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 font-mono text-xs">
              {chipToText(x)}
              <button type="button" aria-label={`Remove filter ${x.key}`} className="text-muted-foreground hover:text-foreground" onClick={() => set({ filters: removeChip(controls.filters, x.key) })}>
                ×
              </button>
            </span>
          ))}
        </div>
        <div className="mt-1 flex items-center gap-1">
          <select className={`${inputClass} w-auto`} aria-label="Filter tag" value={chipDraft.key} onChange={(e) => setChipDraft({ ...chipDraft, key: e.target.value })}>
            <option value="">tag…</option>
            {keys.map((x) => <option key={x} value={x}>{dimensionLabel(x)}</option>)}
          </select>
          <span className="text-muted-foreground">=</span>
          <input className={`${inputClass} font-mono`} aria-label="Filter value" placeholder="value or prefix*" value={chipDraft.value} onChange={(e) => setChipDraft({ ...chipDraft, value: e.target.value })} />
          <button
            type="button"
            className={toggleClass(false)}
            disabled={chip === null}
            onClick={() => {
              if (chip) {
                set({ filters: upsertChip(controls.filters, chip) });
                setChipDraft({ key: chipDraft.key, value: '' });
              }
            }}
          >
            Add
          </button>
        </div>
      </Row>
      <Row label="Compare">
        <div className="flex gap-1">
          <button type="button" className={toggleClass(controls.compareDay)} onClick={() => set({ compareDay: !controls.compareDay })}>1d ago</button>
          <button type="button" className={toggleClass(controls.compareWeek)} onClick={() => set({ compareWeek: !controls.compareWeek })}>7d ago</button>
        </div>
      </Row>
      {kind !== null && supportsPercentile(kind) && !ratio && (
        <Row label="Percentile">
          <select className={inputClass} value={controls.percentile ?? ''} onChange={(e) => set({ percentile: e.target.value ? Number(e.target.value) : null })}>
            <option value="">None</option>
            {PERCENTILES.map((p) => <option key={p} value={p}>{percentileLabel(p)}</option>)}
          </select>
        </Row>
      )}
      {kind !== null && supportsRate(kind) && !ratio && (
        <Row label="Rate">
          <div className="flex gap-1">
            {RATES.map((r) => (
              <button key={r.id} type="button" className={toggleClass(rate === r.id)} onClick={() => set({}, r.id)}>{r.label}</button>
            ))}
          </div>
        </Row>
      )}
      {kind === MetricKind.Gauge && (
        <Row label="Range lines">
          <button type="button" className={toggleClass(controls.showMinMax)} onClick={() => set({ showMinMax: !controls.showMinMax })}>min / max</button>
        </Row>
      )}
      {kind !== null && supportsRatio(kind) && controls.breakdown !== null && (
        <Row label="View">
          <div className="flex items-center gap-1">
            <button type="button" className={toggleClass(!ratio)} onClick={() => set({ view: 'values', numerator: null })}>Values</button>
            <button type="button" className={toggleClass(ratio)} onClick={() => set({ view: 'ratio' })}>Ratio</button>
            {ratio && (
              <DraftInput
                className={`${inputClass} w-32 font-mono`}
                aria-label="Numerator pattern"
                placeholder="numerator e.g. 5*"
                value={controls.numerator ?? ''}
                isValid={(x) => parsePattern(x) !== null}
                onCommit={(numerator) => set({ numerator })}
              />
            )}
          </div>
        </Row>
      )}
      {instrument && (
        <Row label="Preview (the board's range)">
          <ChartTileChart
            instrument={instrument.name}
            kind={instrument.kind}
            unit={instrument.unit}
            query={query}
            selection={selection}
            refetchMs={refetchMs}
            enabled
            deploys={deploys}
            height={160}
          />
        </Row>
      )}
    </div>
  );
}

/**
 * A text control that keeps what is typed locally and writes it back (trimmed, blank = null) only on blur or Enter: a
 * write per keystroke re-fetched the preview on every key, and a numerator that is invalid halfway through typing
 * (`5*0` on the way to `5*`) was dropped by the query round-trip, resetting the field under the cursor. An invalid value
 * stays in the field, marked, and is not written. A new committed `value` (another control rewrote the query) replaces
 * the draft.
 */
function DraftInput({
  value,
  onCommit,
  isValid,
  className,
  placeholder,
  'aria-label': ariaLabel,
}: {
  value: string;
  onCommit: (value: string | null) => void;
  isValid?: (value: string) => boolean;
  className: string;
  placeholder?: string;
  'aria-label': string;
}) {
  const [draft, setDraft] = useState(value);
  const [committed, setCommitted] = useState(value);
  if (value !== committed) {
    setCommitted(value);
    setDraft(value);
  }

  const text = draft.trim();
  const invalid = text !== '' && isValid !== undefined && !isValid(text);
  const commit = () => {
    if (invalid || text === value) {
      return;
    }

    onCommit(text === '' ? null : text);
  };

  return (
    <input
      className={className}
      aria-label={ariaLabel}
      aria-invalid={invalid}
      placeholder={placeholder}
      value={draft}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit();
        }
      }}
    />
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <span className="mb-1 block text-xs text-gray-500">{label}</span>
      {children}
    </div>
  );
}
