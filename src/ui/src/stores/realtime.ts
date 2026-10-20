import { create } from 'zustand';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { getHubUrl, type RealtimeEvent } from '@/api/realtime';
import { emit } from '@/lib/realtimeBus';
import { useDashboardStore } from '@/stores/dashboard';
import { isTabHidden, onTabVisible } from '@/lib/visibility';
import type { DashboardStatistics } from '@/types';

export type RealtimeStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'disabled';

interface RealtimeStore {
  status: RealtimeStatus;
  lastEventAt: number | null;
  connection: HubConnection | null;
  connectIfEnabled: (pushEnabled: boolean) => Promise<void>;
  disconnect: () => Promise<void>;
}

let bootInflight: Promise<void> | null = null;

// An event can arrive without a stats snapshot: the server skips it when it has no dashboard client
// of its own (the event then reached us through a SignalR backplane), or when fetching it failed.
// The 1 Hz /status poll is off while push is connected, so without this the navbar and dashboard
// counts would freeze. At most one fetch per second, with a trailing one so the last event of a
// burst is never dropped; a hidden tab waits and fetches once when shown.
const STATS_FALLBACK_MIN_MS = 1000;
let lastStatsFallbackAt = Number.NEGATIVE_INFINITY;
let statsFallbackTimer: ReturnType<typeof setTimeout> | null = null;
let statsFallbackMissedWhileHidden = false;

function fetchStatsNow() {
  if (isTabHidden()) {
    statsFallbackMissedWhileHidden = true;

    return;
  }

  lastStatsFallbackAt = Date.now();
  void useDashboardStore.getState().fetchStats();
}

function refreshStatsWithoutSnapshot() {
  if (statsFallbackTimer !== null) return;

  const wait = lastStatsFallbackAt + STATS_FALLBACK_MIN_MS - Date.now();
  if (wait <= 0) {
    fetchStatsNow();

    return;
  }

  statsFallbackTimer = setTimeout(() => {
    statsFallbackTimer = null;
    fetchStatsNow();
  }, wait);
}

if (typeof document !== 'undefined') {
  onTabVisible(() => {
    if (!statsFallbackMissedWhileHidden) return;
    statsFallbackMissedWhileHidden = false;
    refreshStatsWithoutSnapshot();
  });
}

/** Test hook: forgets the fallback's throttle state between tests. */
export function resetStatsFallbackForTests() {
  if (statsFallbackTimer !== null) clearTimeout(statsFallbackTimer);
  statsFallbackTimer = null;
  lastStatsFallbackAt = Number.NEGATIVE_INFINITY;
  statsFallbackMissedWhileHidden = false;
}

function bridgeEvent(connection: HubConnection, event: RealtimeEvent) {
  // Each HubConnection is a fresh object — bind unconditionally. A module-level
  // "already bridged" set was previously here as a guard, but that prevented
  // re-binding after onclose → reconnect cycles (the new connection had no
  // handlers). Each connectIfEnabled builds exactly one new connection and is
  // the only call site, so binding is always exactly-once per instance.
  //
  // Server pushes the current DashboardStatistics DTO as the first argument when
  // available — see Warp.Dashboard.Push.DashboardBroadcaster.TryFetchStatsAsync.
  // Writing it straight to the dashboard store eliminates a GET /api/status per
  // client per event. Pages that consume `useDashboardStore.stats` (navbar
  // badges, DashboardPage cards) rerender automatically. Other surfaces (jobs
  // lists, detail pages) still listen via the bus and refetch their own views.
  connection.on(event, (payload?: DashboardStatistics) => {
    if (payload && typeof payload === 'object' && 'totalSucceeded' in payload) {
      useDashboardStore.setState({ stats: payload, error: null });
    } else {
      refreshStatsWithoutSnapshot();
    }
    useRealtimeStore.setState({ lastEventAt: Date.now() });
    emit(event);
  });
}

export const useRealtimeStore = create<RealtimeStore>((set, get) => ({
  status: 'idle',
  lastEventAt: null,
  connection: null,
  connectIfEnabled: async (pushEnabled: boolean) => {
    if (bootInflight) {
      return bootInflight;
    }

    const current = get();
    if (current.status === 'connected' || current.status === 'connecting') {
      return;
    }

    if (!pushEnabled) {
      set({ status: 'disabled' });
      return;
    }

    bootInflight = (async () => {
      set({ status: 'connecting' });

      const connection = new HubConnectionBuilder()
        .withUrl(getHubUrl(), { withCredentials: true })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build();

      connection.onreconnecting(() => set({ status: 'reconnecting' }));
      connection.onreconnected(() => {
        set({ status: 'connected', lastEventAt: Date.now() });
        // Drain-on-reconnect: events missed while disconnected are surfaced once
        // here so subscribed pages refetch. Equivalent to NotificationListenerTask's
        // DrainSignals after a transport reconnect.
        emit('JobFinalized');
        emit('MessageEnqueued');
      });
      connection.onclose(() => set({ status: 'disabled', connection: null }));

      // Wire the two event channels we care about. Idempotent — repeated calls to
      // connectIfEnabled (e.g. after login) reuse the existing bridge bindings.
      bridgeEvent(connection, 'JobFinalized');
      bridgeEvent(connection, 'MessageEnqueued');

      try {
        await connection.start();
        set({ status: 'connected', connection, lastEventAt: Date.now() });
        // Initial-connect drain: any jobs that finalized during probe + start were
        // not delivered to this client (SignalR doesn't replay missed events).
        // Subscribers refetch authoritative state from REST so the dashboard isn't
        // stuck on the pre-connect snapshot until the 30 s safety-net interval.
        // Equivalent to onreconnected's drain, but for the first attach too.
        emit('JobFinalized');
        emit('MessageEnqueued');
      } catch {
        set({ status: 'disabled', connection: null });
      }
    })();

    try {
      await bootInflight;
    } finally {
      bootInflight = null;
    }
  },
  disconnect: async () => {
    const { connection } = get();
    if (connection && connection.state !== HubConnectionState.Disconnected) {
      await connection.stop();
    }
    set({ status: 'idle', connection: null });
  },
}));
