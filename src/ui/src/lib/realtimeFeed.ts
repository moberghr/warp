import { useDashboardStore } from '@/stores/dashboard';
import { useRealtimeStore, type RealtimeStatus } from '@/stores/realtime';

/**
 * Realtime data feed for the dashboard's per-second chart.
 *
 * Owns the two responsibilities needed to keep `useDashboardStore.realtimeData`
 * filled:
 *
 *   1. Freshness — `useDashboardStore.stats` must hold current totals. Either
 *      the SignalR push addon writes them on each `JobFinalized` event, or, when
 *      push is unavailable / reconnecting, this feed polls `fetchStats` at 1 Hz.
 *   2. Sampling — a 1 Hz tick reads the running totals from `stats` and appends
 *      a delta point to `realtimeData`.
 *
 * The chart (`RealtimeChart`) binds to `realtimeData` and never knows which path
 * filled it. `MainLayout` is the single owner of the feed lifecycle so sampling
 * keeps running while the user is on other dashboard pages — the graph looks
 * the same whether they stayed on the dashboard or navigated away and back.
 */

// Sole sampler for `useDashboardStore.realtimeData`. ThroughputChart used to
// run a duplicate 5Hz sampler locally; that was removed so the store sees one
// caller and React only re-renders chart consumers on a single cadence.
const SAMPLE_INTERVAL_MS = 200; // 5Hz
const POLL_INTERVAL_MS = 1000;

let samplerId: ReturnType<typeof setInterval> | null = null;
let pollerId: ReturnType<typeof setInterval> | null = null;
let statusUnsub: (() => void) | null = null;

// "Push is delivering stats right now" — only 'connected' qualifies. During
// 'connecting' / 'reconnecting' / 'disabled' / 'idle' we poll, so the chart
// keeps moving across the gaps where the hub isn't actively pushing payloads.
function pushDelivering(status: RealtimeStatus) {
  return status === 'connected';
}

function applyMode(status: RealtimeStatus) {
  if (pushDelivering(status)) {
    if (pollerId !== null) {
      clearInterval(pollerId);
      pollerId = null;
    }

    return;
  }

  if (pollerId === null) {
    pollerId = setInterval(() => {
      void useDashboardStore.getState().fetchStats();
    }, POLL_INTERVAL_MS);
  }
}

export function startRealtimeFeed() {
  if (samplerId !== null) {
    return;
  }

  samplerId = setInterval(() => {
    useDashboardStore.getState().sampleRate();
  }, SAMPLE_INTERVAL_MS);

  applyMode(useRealtimeStore.getState().status);
  statusUnsub = useRealtimeStore.subscribe((state, prevState) => {
    if (state.status === prevState.status) {
      return;
    }
    applyMode(state.status);
  });
}

export function stopRealtimeFeed() {
  if (samplerId !== null) {
    clearInterval(samplerId);
    samplerId = null;
  }
  if (pollerId !== null) {
    clearInterval(pollerId);
    pollerId = null;
  }
  statusUnsub?.();
  statusUnsub = null;
}
