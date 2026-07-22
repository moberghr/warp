import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { startRealtimeFeed, stopRealtimeFeed } from './realtimeFeed';
import { useDashboardStore } from '@/stores/dashboard';
import { useRealtimeStore } from '@/stores/realtime';

// realtimeFeed keeps module-level sampler/poller timers, so stop it after every test.
describe('realtimeFeed', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    window.location.href = 'http://localhost/'; // ensure not demo mode
    // fetchStats must not hit the network when the poller fires.
    vi.spyOn(useDashboardStore.getState(), 'fetchStats').mockResolvedValue(undefined);
    vi.spyOn(useDashboardStore.getState(), 'sampleRate').mockImplementation(() => {});
  });

  afterEach(() => {
    stopRealtimeFeed();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('samples at 1 Hz once started', () => {
    useRealtimeStore.setState({ status: 'connected' });
    startRealtimeFeed();

    vi.advanceTimersByTime(3000);
    expect(useDashboardStore.getState().sampleRate).toHaveBeenCalledTimes(3);
  });

  it('does NOT poll fetchStats while push is connected', () => {
    useRealtimeStore.setState({ status: 'connected' });
    startRealtimeFeed();

    vi.advanceTimersByTime(3000);
    expect(useDashboardStore.getState().fetchStats).not.toHaveBeenCalled();
  });

  it('polls fetchStats at 1 Hz when push is not delivering (reconnecting)', () => {
    useRealtimeStore.setState({ status: 'reconnecting' });
    startRealtimeFeed();

    vi.advanceTimersByTime(2000);
    expect(useDashboardStore.getState().fetchStats).toHaveBeenCalled();
  });

  it('starts polling when status leaves connected and stops when it returns', () => {
    useRealtimeStore.setState({ status: 'connected' });
    startRealtimeFeed();
    vi.advanceTimersByTime(1000);
    const whileConnected = (useDashboardStore.getState().fetchStats as ReturnType<typeof vi.fn>).mock.calls.length;

    useRealtimeStore.setState({ status: 'reconnecting' });
    vi.advanceTimersByTime(2000);
    const whileReconnecting = (useDashboardStore.getState().fetchStats as ReturnType<typeof vi.fn>).mock.calls.length;
    expect(whileReconnecting).toBeGreaterThan(whileConnected);

    useRealtimeStore.setState({ status: 'connected' });
    const afterReconnect = (useDashboardStore.getState().fetchStats as ReturnType<typeof vi.fn>).mock.calls.length;
    vi.advanceTimersByTime(3000);
    expect((useDashboardStore.getState().fetchStats as ReturnType<typeof vi.fn>).mock.calls.length).toBe(afterReconnect);
  });

  it('is inert in demo mode', () => {
    window.location.href = 'http://localhost/?demo';
    useRealtimeStore.setState({ status: 'reconnecting' });
    startRealtimeFeed();

    vi.advanceTimersByTime(3000);
    expect(useDashboardStore.getState().sampleRate).not.toHaveBeenCalled();
    expect(useDashboardStore.getState().fetchStats).not.toHaveBeenCalled();
  });

  it('stopRealtimeFeed halts sampling', () => {
    useRealtimeStore.setState({ status: 'connected' });
    startRealtimeFeed();
    vi.advanceTimersByTime(1000);
    stopRealtimeFeed();
    (useDashboardStore.getState().sampleRate as ReturnType<typeof vi.fn>).mockClear();

    vi.advanceTimersByTime(3000);
    expect(useDashboardStore.getState().sampleRate).not.toHaveBeenCalled();
  });
});
