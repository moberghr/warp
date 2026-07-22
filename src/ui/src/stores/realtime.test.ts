import { describe, it, expect, beforeEach, vi } from 'vitest';
import type { DashboardStatistics } from '@/types';

// Fake SignalR: a builder whose fluent chain yields one connection whose `.on(event, cb)` we can capture
// and invoke, so we can drive the bridge without a real hub. `vi.hoisted` keeps it stable across the
// hoisted vi.mock factory.
const h = vi.hoisted(() => {
  const handlers: Record<string, (payload?: unknown) => void> = {};
  const conn = {
    state: 'Disconnected' as string,
    on: vi.fn((event: string, cb: (payload?: unknown) => void) => { handlers[event] = cb; }),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
  };
  return { handlers, conn };
});

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return h.conn; }
  }
  return { HubConnectionBuilder, HubConnectionState: { Disconnected: 'Disconnected' }, LogLevel: { Warning: 3 } };
});

import { useRealtimeStore } from './realtime';
import { useDashboardStore } from '@/stores/dashboard';
import { subscribeRealtime } from '@/lib/realtimeBus';

beforeEach(() => {
  for (const k of Object.keys(h.handlers)) delete h.handlers[k];
  h.conn.on.mockClear();
  h.conn.stop.mockClear().mockResolvedValue(undefined);
  h.conn.start.mockClear().mockResolvedValue(undefined);
  h.conn.state = 'Disconnected';
  useRealtimeStore.setState({ status: 'idle', connection: null, lastEventAt: null });
  useDashboardStore.setState({ stats: null });
});

describe('realtime store', () => {
  it('marks status disabled without connecting when push is off', async () => {
    await useRealtimeStore.getState().connectIfEnabled(false);

    expect(useRealtimeStore.getState().status).toBe('disabled');
    expect(h.conn.start).not.toHaveBeenCalled();
  });

  it('connects, wires both event channels, and drains on initial connect', async () => {
    const drained: string[] = [];
    const u1 = subscribeRealtime('JobFinalized', () => drained.push('job'));
    const u2 = subscribeRealtime('MessageEnqueued', () => drained.push('msg'));

    await useRealtimeStore.getState().connectIfEnabled(true);

    expect(useRealtimeStore.getState().status).toBe('connected');
    expect(useRealtimeStore.getState().connection).toBe(h.conn);
    expect(h.conn.start).toHaveBeenCalledOnce();
    expect(h.handlers.JobFinalized).toBeTypeOf('function');
    expect(h.handlers.MessageEnqueued).toBeTypeOf('function');
    // Initial-connect drain fires both events so subscribed pages refetch.
    expect(drained).toEqual(['job', 'msg']);

    u1(); u2();
  });

  it('bridges a pushed event: writes the stats payload, bumps lastEventAt, re-emits on the bus', async () => {
    await useRealtimeStore.getState().connectIfEnabled(true);
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-02-02T00:00:00Z'));

    const busHits: string[] = [];
    const u = subscribeRealtime('JobFinalized', () => busHits.push('hit'));
    const payload = { totalSucceeded: 42, totalFailed: 1 } as unknown as DashboardStatistics;

    h.handlers.JobFinalized(payload);

    expect(useDashboardStore.getState().stats).toBe(payload);
    expect(useRealtimeStore.getState().lastEventAt).toBe(Date.parse('2026-02-02T00:00:00Z'));
    expect(busHits).toEqual(['hit']);

    u();
    vi.useRealTimers();
  });

  it('a non-stats payload still bumps lastEventAt + emits but does not overwrite stats', async () => {
    await useRealtimeStore.getState().connectIfEnabled(true);
    useDashboardStore.setState({ stats: { totalSucceeded: 7 } as unknown as DashboardStatistics });

    const busHits: string[] = [];
    const u = subscribeRealtime('MessageEnqueued', () => busHits.push('hit'));

    h.handlers.MessageEnqueued(undefined);

    expect(useDashboardStore.getState().stats).toEqual({ totalSucceeded: 7 });
    expect(busHits).toEqual(['hit']);
    u();
  });

  it('falls back to disabled when the connection fails to start', async () => {
    h.conn.start.mockRejectedValueOnce(new Error('handshake failed'));

    await useRealtimeStore.getState().connectIfEnabled(true);

    expect(useRealtimeStore.getState().status).toBe('disabled');
    expect(useRealtimeStore.getState().connection).toBeNull();
  });

  it('disconnect stops a live connection and returns to idle', async () => {
    await useRealtimeStore.getState().connectIfEnabled(true);
    h.conn.state = 'Connected';

    await useRealtimeStore.getState().disconnect();

    expect(h.conn.stop).toHaveBeenCalledOnce();
    expect(useRealtimeStore.getState().status).toBe('idle');
    expect(useRealtimeStore.getState().connection).toBeNull();
  });
});
