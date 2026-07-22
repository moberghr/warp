import { describe, it, expect, vi, afterEach } from 'vitest';
import { emit, subscribeRealtime } from './realtimeBus';

describe('realtimeBus', () => {
  // The bus holds module-level listeners; track + release each test's subs so they don't leak across tests.
  const cleanups: Array<() => void> = [];
  const sub = (event: Parameters<typeof subscribeRealtime>[0], handler: () => void) => {
    cleanups.push(subscribeRealtime(event, handler));
  };

  afterEach(() => {
    while (cleanups.length) cleanups.pop()!();
    vi.restoreAllMocks();
  });

  it('delivers an emitted event to subscribers', () => {
    const a = vi.fn();
    const b = vi.fn();
    sub('JobFinalized', a);
    sub('JobFinalized', b);

    emit('JobFinalized');

    expect(a).toHaveBeenCalledOnce();
    expect(b).toHaveBeenCalledOnce();
  });

  it('only notifies subscribers of the emitted event kind', () => {
    const job = vi.fn();
    const msg = vi.fn();
    sub('JobFinalized', job);
    sub('MessageEnqueued', msg);

    emit('MessageEnqueued');

    expect(msg).toHaveBeenCalledOnce();
    expect(job).not.toHaveBeenCalled();
  });

  it('stops delivering after unsubscribe', () => {
    const handler = vi.fn();
    const unsub = subscribeRealtime('JobFinalized', handler);

    unsub();
    emit('JobFinalized');

    expect(handler).not.toHaveBeenCalled();
  });

  it('emitting with no subscribers is a no-op', () => {
    expect(() => emit('MessageEnqueued')).not.toThrow();
  });

  it('isolates a throwing subscriber so others still fire', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const bad = vi.fn(() => { throw new Error('boom'); });
    const good = vi.fn();
    sub('JobFinalized', bad);
    sub('JobFinalized', good);

    expect(() => emit('JobFinalized')).not.toThrow();
    expect(good).toHaveBeenCalledOnce();
  });
});
