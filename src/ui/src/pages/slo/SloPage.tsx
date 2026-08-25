import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getSlos, upsertSlo, deleteSlo, ackSlo } from '@/api';
import {
  SloKind,
  SloKindLabel,
  SloState,
  type SloObjective,
  type SloUpsertRequest,
  isRatioKind,
} from '@/types/slo';
import { formatObjectiveValue, formatBudget, SloStatePill, inputClass } from './shared';
import { PageHeading } from '@/components/PageHeading';

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
  const saveMutation = useMutation({ mutationFn: upsertSlo, onSuccess: () => { setDraft(null); invalidate(); } });
  const deleteMutation = useMutation({ mutationFn: deleteSlo, onSuccess: invalidate });
  const ackMutation = useMutation({ mutationFn: (id: number) => ackSlo(id, 60), onSuccess: invalidate });

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
          onClick={() => setDraft({ ...emptyDraft })}
          className="px-4 py-2 rounded-lg bg-blue-600 text-white text-sm font-medium hover:bg-blue-700"
        >
          New objective
        </button>
      </div>

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
                  onEdit={() => setDraft(toDraft(o))}
                  onDelete={() => deleteMutation.mutate(o.id)}
                  onAck={() => ackMutation.mutate(o.id)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {draft && (
        <ObjectiveForm
          draft={draft}
          onChange={setDraft}
          onCancel={() => setDraft(null)}
          onSave={() => saveMutation.mutate(draft)}
          saving={saveMutation.isPending}
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
  const budget = o.budgetRemaining;
  const barColor = budget < 0 ? 'bg-red-500' : budget < 0.25 ? 'bg-amber-500' : 'bg-green-500';
  const width = Math.max(0, Math.min(100, budget * 100));

  return (
    <tr className="hover:bg-gray-50 dark:hover:bg-gray-900/50">
      <td className="px-4 py-3">
        <Link to={`/slo/${o.id}`} className="font-medium text-blue-600 hover:underline">{o.name}</Link>
        <div className="text-xs text-gray-500">{SloKindLabel[o.kind]}{o.percentile ? ` p${o.percentile}` : ''}</div>
      </td>
      <td className="px-4 py-3">
        <span className="font-mono text-xs">{o.dimension}</span>
        {o.application && <span className="ml-1 text-xs text-gray-400">@{o.application}</span>}
      </td>
      <td className="px-4 py-3 text-right tabular-nums">{formatObjectiveValue(o.kind, o.targetValue)}</td>
      <td className="px-4 py-3 text-right tabular-nums">{o.evaluated ? formatObjectiveValue(o.kind, o.attainment) : '—'}</td>
      <td className="px-4 py-3">
        <div className="flex items-center gap-2">
          <div className="h-2 w-24 rounded bg-gray-200 dark:bg-gray-700 overflow-hidden">
            <div className={`h-full ${barColor}`} style={{ width: `${width}%` }} />
          </div>
          <span className="text-xs tabular-nums w-12 text-right">{o.evaluated ? formatBudget(budget) : '—'}</span>
        </div>
      </td>
      <td className="px-4 py-3 text-right tabular-nums text-xs">
        {o.evaluated ? `${o.burnRateShort.toFixed(1)}× / ${o.burnRateLong.toFixed(1)}×` : '—'}
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
}: {
  draft: SloUpsertRequest;
  onChange: (d: SloUpsertRequest) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
}) {
  const set = (patch: Partial<SloUpsertRequest>) => onChange({ ...draft, ...patch });
  const ratio = isRatioKind(draft.kind);

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4" onClick={onCancel}>
      <div className="bg-white dark:bg-gray-900 rounded-xl p-6 w-full max-w-md shadow-xl" onClick={(e) => e.stopPropagation()}>
        <h2 className="text-lg font-semibold mb-4">{draft.id ? 'Edit objective' : 'New objective'}</h2>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <input className={inputClass} value={draft.name} onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label="Kind">
            <select className={inputClass} value={draft.kind} onChange={(e) => set({ kind: Number(e.target.value) as SloKind, percentile: null })}>
              {Object.entries(SloKindLabel).map(([k, label]) => (
                <option key={k} value={k}>{label}</option>
              ))}
            </select>
          </Field>
          <Field label="Dimension (queue / job type / *)">
            <input className={`${inputClass} font-mono`} value={draft.dimension} onChange={(e) => set({ dimension: e.target.value })} />
          </Field>
          <Field label={ratio ? 'Target ratio (0–1)' : 'Target (ms / count)'}>
            <input className="input tabular-nums" type="number" step="any" value={draft.targetValue} onChange={(e) => set({ targetValue: Number(e.target.value) })} />
          </Field>
          {(draft.kind === SloKind.QueueWaitLatency || draft.kind === SloKind.ExecutionLatency) && (
            <Field label="Percentile (90 / 95 / 99)">
              <input className="input tabular-nums" type="number" value={draft.percentile ?? 95} onChange={(e) => set({ percentile: Number(e.target.value) })} />
            </Field>
          )}
          <Field label="Window (seconds)">
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
        <div className="flex justify-end gap-2 mt-6">
          <button type="button" onClick={onCancel} className="px-4 py-2 rounded-lg text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button type="button" onClick={onSave} disabled={saving || !draft.name || !draft.dimension} className="px-4 py-2 rounded-lg bg-blue-600 text-white text-sm font-medium hover:bg-blue-700 disabled:opacity-50">
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
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

function toDraft(o: SloObjective): SloUpsertRequest {
  return {
    id: o.id,
    name: o.name,
    kind: o.kind,
    dimension: o.dimension,
    application: o.application,
    targetValue: o.targetValue,
    percentile: o.percentile,
    windowSeconds: o.windowSeconds,
    enabled: o.enabled,
  };
}
