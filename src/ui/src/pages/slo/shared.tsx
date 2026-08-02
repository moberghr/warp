import { SloKind, SloState, SloStateLabel, isRatioKind } from '@/types/slo';

export const inputClass =
  'w-full rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 py-1.5 text-sm';

/** Formats a target/observed value per kind: ratio kinds as a %, latency as ms, depth as a count. */
export function formatObjectiveValue(kind: SloKind, value: number): string {
  if (isRatioKind(kind)) {
    return `${(value * 100).toFixed(2)}%`;
  }
  if (kind === SloKind.BacklogDepth) {
    return `${Math.round(value)}`;
  }
  return `${Math.round(value)} ms`;
}

export function formatBudget(budget: number): string {
  return `${Math.round(budget * 100)}%`;
}

export function SloStatePill({ state, enabled }: { state: SloState; enabled: boolean }) {
  if (!enabled) {
    return (
      <span className="inline-block px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400">
        Disabled
      </span>
    );
  }

  const styles: Record<SloState, string> = {
    [SloState.Healthy]: 'bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300',
    [SloState.Warning]: 'bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300',
    [SloState.Breaching]: 'bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300',
    [SloState.Acknowledged]: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300',
  };

  return (
    <span className={`inline-block px-2 py-0.5 rounded-full text-xs font-medium ${styles[state]}`}>
      {SloStateLabel[state]}
    </span>
  );
}
