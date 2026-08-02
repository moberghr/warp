// Static SLO fixtures (§8.31) for hosted demo mode + docs screenshots. Mirrors SloList / SloObjective
// (src/ui/src/types/slo.ts). Values are hand-picked to show one healthy, one warning, and one breaching
// objective so the state pills and budget bars all appear on the /slo page.
import { SloKind, SloState, type SloObjective, type SloList } from '@/types/slo';

const objectives: SloObjective[] = [
  {
    id: 1,
    name: 'Email success rate ≥ 99.5%',
    kind: SloKind.SuccessRate,
    dimension: 'Shop.Jobs.SendReceiptEmail',
    application: null,
    targetValue: 0.995,
    percentile: null,
    windowSeconds: 3600,
    enabled: true,
    evaluated: true,
    attainment: 0.9986,
    budgetRemaining: 0.72,
    burnRateShort: 0.4,
    burnRateLong: 0.28,
    state: SloState.Healthy,
    acknowledgedUntil: null,
    lastEvaluatedAt: '2026-07-31T09:14:00Z',
  },
  {
    id: 2,
    name: 'Default queue-wait p95 < 30s',
    kind: SloKind.QueueWaitLatency,
    dimension: 'default',
    application: null,
    targetValue: 30000,
    percentile: 95,
    windowSeconds: 3600,
    enabled: true,
    evaluated: true,
    attainment: 26840,
    budgetRemaining: 0.34,
    burnRateShort: 1.6,
    burnRateLong: 0.66,
    state: SloState.Warning,
    acknowledgedUntil: null,
    lastEvaluatedAt: '2026-07-31T09:14:00Z',
  },
  {
    id: 3,
    name: 'Charge deadline attainment ≥ 99%',
    kind: SloKind.DeadlineAttainment,
    dimension: 'Shop.Jobs.ChargeOrder',
    application: null,
    targetValue: 0.99,
    percentile: null,
    windowSeconds: 21600,
    enabled: true,
    evaluated: true,
    attainment: 0.981,
    budgetRemaining: -0.9,
    burnRateShort: 4.2,
    burnRateLong: 1.9,
    state: SloState.Breaching,
    acknowledgedUntil: null,
    lastEvaluatedAt: '2026-07-31T09:14:00Z',
  },
];

export const demoSlos = (): SloList => ({ items: objectives.map((o) => ({ ...o })) });

export const demoSloDetail = (id: number): SloObjective | undefined => {
  const found = objectives.find((o) => o.id === id);

  return found ? { ...found } : undefined;
};
