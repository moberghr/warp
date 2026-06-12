import { create } from 'zustand';
import type { HubConnection } from '@microsoft/signalr';
import { getHubUrl, type RealtimeEvent } from '@/api/realtime';
import { emit } from '@/lib/realtimeBus';
import { useDashboardStore } from '@/stores/dashboard';
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

function bridgeEvent(connection: HubConnection, event: RealtimeEvent) {
  // Each HubConnection is a fresh object — bind unconditionally. A module-level
  // "already bridged" set was previously here as a guard, but that prevented
  // re-binding after onclose → reconnect cycles (the new connection had no
  // handlers). Each connectIfEnabled builds exactly one new connection and is
  // the only call site, so binding is always exactly-once per instance.
  //
  // Server pushes the current DashboardStatistics DTO as the first argument when
  // available — see Warp.UI.DashboardPush.DashboardBroadcaster.TryFetchStatsAsync.
  // Writing it straight to the dashboard store eliminates a GET /api/status per
  // client per event. Pages that consume `useDashboardStore.stats` (navbar
  // badges, DashboardPage cards) rerender automatically. Other surfaces (jobs
  // lists, detail pages) still listen via the bus and refetch their own views.
  connection.on(event, (payload?: DashboardStatistics) => {
    if (payload && typeof payload === 'object' && 'totalSucceeded' in payload) {
      useDashboardStore.setState({ stats: payload, error: null });
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

      // Lazy-load SignalR so deployments without push (UseDatabasePush off, or
      // pushEnabled=false from /api/addons) never ship the ~70 KB transport.
      const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');

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
    if (connection) {
      const { HubConnectionState } = await import('@microsoft/signalr');
      if (connection.state !== HubConnectionState.Disconnected) {
        await connection.stop();
      }
    }
    set({ status: 'idle', connection: null });
  },
}));
