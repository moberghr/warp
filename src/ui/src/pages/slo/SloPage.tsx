import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getSlos, upsertSlo, deleteSlo, ackSlo, getMetrics } from '@/api';
import { queryKeys } from '@/lib/queryClient';
import type { MetricInstrument } from '@/types/metrics';
import {
  SloComparison,
  SloGaugeAggregate,
  SloKind,
  SloKindLabel,
  SloState,
  type SloObjective,
  type SloUpsertRequest,
  isMetricKind,
  isRatioKind,
} from '@/types/slo';
import { SloStatePill, inputClass } from './shared';
import { PageHeading } from '@/components/PageHeading';
import { errorMessage } from '@/lib/apiError';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import {
  MIN_METRIC_WINDOW_SECONDS,
  buildUpsertRequest,
  changeKind,
  DEFAULT_LATENCY_PERCENTILE,
  fieldsFor,
  fractionToPercent,
  objectiveToDraft,
  percentToFraction,
  selectableInstruments,
  validateMetricDraft,
} from './sloForm';
import { formatBudget, formatObserved, formatTarget, showsSloValues } from './sloDisplay';

const emptyDraft: SloUpsertRequest = {
  id: 0,
  name: '',
  kind: SloKind.SuccessRate,
  dimension: '',
  application: null,
  targetValue: 0.99,
  percentile: null,
  windowSeconds: 3600,
  enabled: true,
};

export default function SloPage() {
  const queryClient = useQueryClient();
  const { data, isLoading, isError } = useQuery({
    queryKey: ['slos'],
    queryFn: getSlos,
    refetchInterval: 30_000,
  });

  const [draft, setDraft] = useState<SloUpsertRequest | null>(null);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['slos'] });
  const saveMutation = useMutation({ mutationFn: (d: SloUpsertRequest) => upsertSlo(buildUpsertRequest(d)), onSuccess: () => { setDraft(null); invalidate(); } });
  const openDraft = (d: SloUpsertRequest | null) => { saveMutation.reset(); setDraft(d); };
  // Settled, not success: a failed delete or ack may still have changed the row (or the row is already gone), so the
  // list is re-read either way and the error is shown above it.
  const deleteMutation = useMutation({ mutationFn: deleteSlo, onSettled: invalidate });
  const ackMutation = useMutation({ mutationFn: (id: number) => ackSlo(id, 60), onSettled: invalidate });
  const [pendingDelete, setPendingDelete] = useState<SloObjective | null>(null);

  const objectives = data?.items ?? [];

  return (
    <div className="p-6 max-w-6xl mx-auto">
      <div className="flex items-center justify-between mb-6">
        <div>
          <PageHeading className="">Service-level objectives</PageHeading>
          <p className="text-sm text-gray-500 mt-1">
            Error-budget tracking with multi-window burn-rate alerting (§8.31).
          </p>
        </div>
        <button
          type="button"
          onClick={() => openDraft({ ...emptyDraft })}
          className="px-4 py-2 rounded-lg bg-blue-600 text-white text-sm font-medium hover:bg-blue-700"
        >
          New objective
        </button>
      </div>

      {deleteMutation.isError && (
        <p role="alert" className="mb-3 text-sm text-red-600">{errorMessage(deleteMutation.error, 'Failed to delete the objective.')}</p>
      )}
      {ackMutation.isError && (
        <p role="alert" className="mb-3 text-sm text-red-600">{errorMessage(ackMutation.error, 'Failed to acknowledge the objective.')}</p>
      )}
      {isLoading && <p className="text-gray-500">Loading…</p>}
      {isError && <p className="text-red-600">Failed to load SLO objectives.</p>}

      {!isLoading && !isError && objectives.length === 0 && (
        <div className="rounded-lg border border-dashed border-gray-300 dark:border-gray-700 p-10 text-center text-gray-500">
          No objectives yet. Create one, or seed them in code with <code>opt.AddSlo(o =&gt; o.AddObjective(...))</code>.
        </div>
      )}

      {objectives.length > 0 && (
        <div className="overflow-x-auto rounded-lg border border-gray-200 dark:border-gray-800">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-900 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                <th className="text-left px-4 py-2">Objective</th>
                <th className="text-left px-4 py-2">Scope</th>
                <th className="text-right px-4 py-2">Target</th>
                <th className="text-right px-4 py-2">Observed</th>
                <th className="text-left px-4 py-2">Budget</th>
                <th className="text-right px-4 py-2">Burn (fast / slow)</th>
                <th className="text-left px-4 py-2">State</th>
                <th className="px-4 py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 dark:divide-gray-800">
              {objectives.map((o) => (
                <ObjectiveRow
                  key={o.id}
                  objective={o}
                  onEdit={() => openDraft(objectiveToDraft(o))}
                  onDelete={() => setPendingDelete(o)}
                  onAck={() => ackMutation.mutate(o.id)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        onOpenChange={(open) => !open && setPendingDelete(null)}
        title={`Delete objective "${pendingDelete?.name ?? ''}"?`}
        description="Its evaluation and its notes are deleted with it. An objective seeded in code is re-created on the next start."
        confirmLabel="Delete"
        variant="destructive"
        onConfirm={() => {
          if (pendingDelete) {
            deleteMutation.mutate(pendingDelete.id);
          }
          setPendingDelete(null);
        }}
      />

      {draft && (
        <ObjectiveForm
          draft={draft}
          onChange={setDraft}
          onCancel={() => openDraft(null)}
          onSave={() => saveMutation.mutate(draft)}
          saving={saveMutation.isPending}
          serverError={saveMutation.isError ? errorMessage(saveMutation.error) : null}
        />
      )}
    </div>
  );
}

function ObjectiveRow({
  objective,
  onEdit,
  onDelete,
  onAck,
}: {
  objective: SloObjective;
  onEdit: () => void;
  onDelete: () => void;
  onAck: () => void;
}) {
  const o = objective;
  const hasValues = showsSloValues(o);
  const budget = o.budgetRemaining;
  const barColor = budget < 0 ? 'bg-red-500' : budget < 0.25 ? 'bg-amber-500' : 'bg-green-500';
  const width = Math.max(0, Math.min(100, budget * 100));

  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-900/50">
      <td className="px-4 py-3">
        <Link to={`/slo/${o.id}`} className="font-medium text-blue-600 hover:underline">{o.name}</Link>
        <div className="text-xs text-gray-500">{SloKindLabel[o.kind]}{o.percentile && !isMetricKind(o.kind) ? ` p${o.percentile}` : ''}</div>
      </td>
      <td className="px-4 py-3">
        <span className="font-mono text-xs">{o.dimension}</span>
        {o.application && <span className="ml-1 text-xs text-gray-400">@{o.application}</span>}
      </td>
      <td className="px-4 py-3 text-right tabular-nums">{formatTarget(o)}</td>
      <td className="px-4 py-3 text-right tabular-nums">{hasValues ? formatObserved(o) : '—'}</td>
      <td className="px-4 py-3">
        <div className="flex items-center gap-2">
          <div className="h-2 w-24 rounded bg-gray-200 dark:bg-gray-700 overflow-hidden">
            {hasValues && <div className={`h-full ${barColor}`} style={{ width: `${width}%` }} />}
          </div>
          <span className="text-xs tabular-nums w-12 text-right">{hasValues ? formatBudget(budget) : '—'}</span>
        </div>
      </td>
      <td className="px-4 py-3 text-right tabular-nums text-xs">
        {hasValues ? `${o.burnRateShort.toFixed(1)}× / ${o.burnRateLong.toFixed(1)}×` : '—'}
      </td>
      <td className="px-4 py-3"><SloStatePill state={o.state} enabled={o.enabled} /></td>
      <td className="px-4 py-3 text-right whitespace-nowrap">
        {o.state === SloState.Breaching && (
          <button type="button" onClick={onAck} className="text-xs text-amber-600 hover:underline mr-3">Ack 1h</button>
        )}
        <button type="button" onClick={onEdit} className="text-xs text-gray-600 hover:underline mr-3">Edit</button>
        <button type="button" onClick={onDelete} className="text-xs text-red-600 hover:underline">Delete</button>
      </td>
    </tr>
  );
}

function ObjectiveForm({
  draft,
  onChange,
  onCancel,
  onSave,
  saving,
  serverError,
}: {
  draft: SloUpsertRequest;
  onChange: (d: SloUpsertRequest) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
  serverError: string | null;
}) {
  const set = (patch: Partial<SloUpsertRequest>) => onChange({ ...draft, ...patch });
  const metric = isMetricKind(draft.kind);
  const fields = fieldsFor(draft.kind);
  const ratio = isRatioKind(draft.kind);

  const { data: metrics } = useQuery({ queryKey: queryKeys.metrics, queryFn: getMetrics, enabled: metric });
  const instruments = selectableInstruments(metrics ?? [], draft.kind);
  const primary = instruments.find((x) => x.name === draft.dimension);
  const clientError = validateMetricDraft(draft);
  const saveDisabled = saving || !draft.name || !draft.dimension || clientError !== null;

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4" onClick={onCancel}>
      <div className="bg-white dark:bg-gray-900 rounded-xl p-6 w-full max-w-md max-h-[90vh] overflow-y-auto shadow-xl" onClick={(e) => e.stopPropagation()}>
        <h2 className="text-lg font-semibold mb-4">{draft.id ? 'Edit objective' : 'New objective'}</h2>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <input className={inputClass} value={draft.name} onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label="Kind">
            <select className={inputClass} value={draft.kind} onChange={(e) => onChange(changeKind(draft, Number(e.target.value) as SloKind))}>
              {Object.entries(SloKindLabel).map(([k, label]) => (
                <option key={k} value={k}>{label}</option>
              ))}
            </select>
          </Field>

          {metric ? (
            <>
              <Field label={fields.secondary ? 'Primary instrument' : 'Instrument'}>
                <InstrumentSelect value={draft.dimension} instruments={instruments} onChange={(dimension) => set({ dimension })} />
              </Field>
              {fields.ratio && (
                <>
                  <Field label="Ratio tag">
                    <TagKeyInput value={draft.ratioTag ?? ''} keys={primary?.tagKeys ?? []} onChange={(ratioTag) => set({ ratioTag })} />
                  </Field>
                  <Field label="Good values (comma-separated, trailing * allowed)">
                    <input className={`${inputClass} font-mono`} placeholder="success,authorised" value={draft.goodValues ?? ''} onChange={(e) => set({ goodValues: e.target.value })} />
                  </Field>
                  <Field label="Bad values (comma-separated, trailing * allowed)">
                    <input className={`${inputClass} font-mono`} placeholder="failed,refused" value={draft.badValues ?? ''} onChange={(e) => set({ badValues: e.target.value })} />
                  </Field>
                  <Field label="Compare with baseline (optional)">
                    <BaselineSelect value={draft.baselineDays ?? null} allowNone onChange={(baselineDays) => set({ baselineDays })} />
                  </Field>
                </>
              )}
              {fields.tagFilter && (
                <Field label={fields.secondary ? 'Primary tag filter (k=v,k=v; trailing * allowed)' : 'Tag filter (k=v,k=v; trailing * allowed)'}>
                  <input className={`${inputClass} font-mono`} placeholder="http.response.status_code=5*" value={draft.tagFilter ?? ''} onChange={(e) => set({ tagFilter: e.target.value })} />
                </Field>
              )}
              {fields.baseline === 'required' && (
                <Field label="Baseline">
                  <BaselineSelect value={draft.baselineDays ?? null} onChange={(baselineDays) => set({ baselineDays })} />
                </Field>
              )}
              {fields.secondary && (
                <>
                  <Field label="Secondary instrument">
                    <InstrumentSelect value={draft.secondaryMetric ?? ''} instruments={instruments} onChange={(secondaryMetric) => set({ secondaryMetric })} />
                  </Field>
                  <Field label="Secondary tag filter">
                    <input className={`${inputClass} font-mono`} value={draft.secondaryTagFilter ?? ''} onChange={(e) => set({ secondaryTagFilter: e.target.value })} />
                  </Field>
                </>
              )}
              {draft.kind === SloKind.MetricGaugeThreshold && (
                <>
                  <Field label="Compare the per-bucket">
                    <select className={inputClass} value={draft.gaugeAggregate ?? SloGaugeAggregate.Average} onChange={(e) => set({ gaugeAggregate: Number(e.target.value) as SloGaugeAggregate })}>
                      <option value={SloGaugeAggregate.Average}>Average (samples from every process averaged)</option>
                      <option value={SloGaugeAggregate.Min}>Minimum</option>
                      <option value={SloGaugeAggregate.Max}>Maximum</option>
                    </select>
                  </Field>
                  <Field label="Must stay">
                    <select className={inputClass} value={draft.comparison ?? SloComparison.AtMost} onChange={(e) => set({ comparison: Number(e.target.value) as SloComparison })}>
                      <option value={SloComparison.AtMost}>At most the threshold</option>
                      <option value={SloComparison.AtLeast}>At least the threshold</option>
                    </select>
                  </Field>
                </>
              )}
              {fields.percentile && (
                <Field label={draft.kind === SloKind.MetricGaugeThreshold ? 'Compliant share of the window (%, 1–99)' : 'Percentile (1–99)'}>
                  <input className={`${inputClass} tabular-nums`} type="number" min={1} max={99} value={draft.percentile ?? DEFAULT_LATENCY_PERCENTILE} onChange={(e) => set({ percentile: Number(e.target.value) })} />
                </Field>
              )}
              <MetricTarget draft={draft} onChange={(targetValue) => set({ targetValue })} />
            </>
          ) : (
            <>
              <Field label="Dimension (queue / job type / *)">
                <input className={`${inputClass} font-mono`} value={draft.dimension} onChange={(e) => set({ dimension: e.target.value })} />
              </Field>
              <Field label={ratio ? 'Target ratio (0–1)' : 'Target (ms / count)'}>
                <input className="input tabular-nums" type="number" step="any" value={draft.targetValue} onChange={(e) => set({ targetValue: Number(e.target.value) })} />
              </Field>
              {fields.percentile && (
                <Field label="Percentile (90 / 95 / 99)">
                  <input className="input tabular-nums" type="number" value={draft.percentile ?? 95} onChange={(e) => set({ percentile: Number(e.target.value) })} />
                </Field>
              )}
            </>
          )}

          <Field label={metric ? `Window (seconds, minimum ${MIN_METRIC_WINDOW_SECONDS})` : 'Window (seconds)'}>
            <input className="input tabular-nums" type="number" value={draft.windowSeconds} onChange={(e) => set({ windowSeconds: Number(e.target.value) })} />
          </Field>
          <Field label="Application (optional)">
            <input className={inputClass} value={draft.application ?? ''} onChange={(e) => set({ application: e.target.value || null })} />
          </Field>
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={draft.enabled} onChange={(e) => set({ enabled: e.target.checked })} />
            <span>Enabled</span>
          </label>
        </div>
        {clientError && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
        {serverError && <p role="alert" className="mt-2 text-sm text-red-600">{serverError}</p>}
        <div className="flex justify-end gap-2 mt-6">
          <button type="button" onClick={onCancel} className="px-4 py-2 rounded-lg text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button type="button" onClick={onSave} disabled={saveDisabled} className="px-4 py-2 rounded-lg bg-blue-600 text-white text-sm font-medium hover:bg-blue-700 disabled:opacity-50">
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function InstrumentSelect({
  value,
  instruments,
  onChange,
}: {
  value: string;
  instruments: MetricInstrument[];
  onChange: (name: string) => void;
}) {
  // An objective may reference an instrument that has not reported yet (or aged out of the registry): keep it selectable.
  const known = instruments.some((x) => x.name === value);

  return (
    <select className={`${inputClass} font-mono`} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Select an instrument…</option>
      {value !== '' && !known && <option value={value}>{value}</option>}
      {instruments.map((x) => (
        <option key={x.name} value={x.name}>{x.name}</option>
      ))}
    </select>
  );
}

function TagKeyInput({ value, keys, onChange }: { value: string; keys: string[]; onChange: (v: string) => void }) {
  if (keys.length === 0) {
    return <input className={`${inputClass} font-mono`} value={value} onChange={(e) => onChange(e.target.value)} />;
  }

  return (
    <select className={`${inputClass} font-mono`} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Select a tag…</option>
      {value !== '' && !keys.includes(value) && <option value={value}>{value}</option>}
      {keys.map((k) => (
        <option key={k} value={k}>{k}</option>
      ))}
    </select>
  );
}

function BaselineSelect({
  value,
  allowNone,
  onChange,
}: {
  value: number | null;
  allowNone?: boolean;
  onChange: (days: number | null) => void;
}) {
  return (
    <select className={inputClass} value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))}>
      {allowNone && <option value="">No baseline</option>}
      <option value="1">Same window 1 day earlier</option>
      <option value="7">Same window 7 days earlier</option>
    </select>
  );
}

function MetricTarget({ draft, onChange }: { draft: SloUpsertRequest; onChange: (target: number) => void }) {
  const percent = isRatioKind(draft.kind);
  const label =
    draft.kind === SloKind.MetricRatio
      ? draft.baselineDays == null
        ? 'Target (good ratio ≥ %)'
        : 'Max drop vs baseline (percentage points)'
      : draft.kind === SloKind.MetricVolumeFloor
        ? 'Minimum fraction of baseline (%)'
        : draft.kind === SloKind.MetricGap
          ? 'Max gap (primary − secondary events)'
          : draft.kind === SloKind.MetricLatency
            ? "Threshold (instrument's unit; compared with bucket upper bounds)"
            : draft.kind === SloKind.MetricGaugeThreshold
              ? 'Threshold (gauge value)'
              : 'Max events in window';

  return (
    <Field label={label}>
      <input
        className={`${inputClass} tabular-nums`}
        type="number"
        step="any"
        value={percent ? fractionToPercent(draft.targetValue) : draft.targetValue}
        onChange={(e) => onChange(percent ? percentToFraction(Number(e.target.value)) : Number(e.target.value))}
      />
    </Field>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="block text-xs text-gray-500 mb-1">{label}</span>
      {children}
    </label>
  );
}
