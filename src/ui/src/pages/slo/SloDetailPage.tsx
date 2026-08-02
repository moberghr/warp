import { Link, useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getSlo, ackSlo } from '@/api';
import { SloKind, SloKindLabel, SloState } from '@/types/slo';
import { formatObjectiveValue, formatBudget, SloStatePill } from './shared';

export default function SloDetailPage() {
  const { id } = useParams<{ id: string }>();
  const objectiveId = Number(id);
  const queryClient = useQueryClient();

  const { data: o, isLoading, isError } = useQuery({
    queryKey: ['slo', objectiveId],
    queryFn: () => getSlo(objectiveId),
    refetchInterval: 30_000,
  });

  const ackMutation = useMutation({
    mutationFn: () => ackSlo(objectiveId, 60),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['slo', objectiveId] }),
  });

  if (isLoading) {
    return <div className="p-6 text-gray-500">Loading…</div>;
  }
  if (isError || !o) {
    return (
      <div className="p-6">
        <Link to="/slo" className="text-blue-600 hover:underline text-sm">← SLOs</Link>
        <p className="text-red-600 mt-4">Objective not found.</p>
      </div>
    );
  }

  const budget = o.budgetRemaining;
  const barColor = budget < 0 ? 'bg-red-500' : budget < 0.25 ? 'bg-amber-500' : 'bg-green-500';
  const width = Math.max(0, Math.min(100, budget * 100));

  return (
    <div className="p-6 max-w-3xl mx-auto">
      <Link to="/slo" className="text-blue-600 hover:underline text-sm">← SLOs</Link>

      <div className="flex items-start justify-between mt-3 mb-6">
        <div>
          <h1 className="text-2xl font-semibold">{o.name}</h1>
          <p className="text-sm text-gray-500 mt-1">
            {SloKindLabel[o.kind]}{o.percentile ? ` p${o.percentile}` : ''} · <span className="font-mono">{o.dimension}</span>
            {o.application && <span className="text-gray-400"> @{o.application}</span>}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <SloStatePill state={o.state} enabled={o.enabled} />
          {o.state === SloState.Breaching && (
            <button type="button" onClick={() => ackMutation.mutate()} className="px-3 py-1.5 rounded-lg bg-amber-500 text-white text-sm hover:bg-amber-600">
              Acknowledge 1h
            </button>
          )}
        </div>
      </div>

      {!o.evaluated ? (
        <div className="rounded-lg border border-dashed border-gray-300 dark:border-gray-700 p-8 text-center text-gray-500">
          Not evaluated yet — the SloEvaluator will produce a status on its next tick.
        </div>
      ) : (
        <>
          <div className="rounded-xl border border-gray-200 dark:border-gray-800 p-5 mb-4">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm text-gray-500">Error budget remaining</span>
              <span className="text-sm tabular-nums font-medium">{formatBudget(budget)}</span>
            </div>
            <div className="h-3 rounded bg-gray-200 dark:bg-gray-700 overflow-hidden">
              <div className={`h-full ${barColor}`} style={{ width: `${width}%` }} />
            </div>
          </div>

          <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
            <Stat label="Target" value={formatObjectiveValue(o.kind, o.targetValue)} />
            <Stat label="Observed" value={formatObjectiveValue(o.kind, o.attainment)} />
            <Stat label="Burn (fast)" value={`${o.burnRateShort.toFixed(2)}×`} danger={o.burnRateShort > 1} />
            <Stat label="Burn (slow)" value={`${o.burnRateLong.toFixed(2)}×`} danger={o.burnRateLong > 1} />
          </div>
        </>
      )}

      <div className="mt-6 text-xs text-gray-500 space-y-1">
        <div>Window: {Math.round(o.windowSeconds / 60)} min · fast-burn window: {Math.max(5, Math.round(o.windowSeconds / 12 / 60))} min</div>
        {o.lastEvaluatedAt && <div>Last evaluated: {new Date(o.lastEvaluatedAt).toLocaleString()}</div>}
        {o.acknowledgedUntil && <div>Acknowledged until: {new Date(o.acknowledgedUntil).toLocaleString()}</div>}
        {isThreshold(o.kind) && <div>Latency/depth objectives compare the windowed observed value to the target (lower is better).</div>}
      </div>
    </div>
  );
}

function Stat({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div className="rounded-lg border border-gray-200 dark:border-gray-800 p-3">
      <div className="text-xs text-gray-500">{label}</div>
      <div className={`text-lg font-semibold tabular-nums ${danger ? 'text-red-600' : ''}`}>{value}</div>
    </div>
  );
}

function isThreshold(kind: SloKind): boolean {
  return kind === SloKind.QueueWaitLatency || kind === SloKind.ExecutionLatency || kind === SloKind.BacklogDepth;
}
