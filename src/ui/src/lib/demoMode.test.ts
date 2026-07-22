import { describe, it, expect, afterEach, vi } from 'vitest';
import { isDemoMode, freezeClock, FROZEN_NOW } from './demoMode';

describe('isDemoMode', () => {
  afterEach(() => {
    window.location.href = 'http://localhost/';
    vi.unstubAllEnvs();
  });

  it('is true when the ?demo query flag is present', () => {
    window.location.href = 'http://localhost/?demo';
    expect(isDemoMode()).toBe(true);
  });

  it('is false with neither ?demo nor VITE_DEMO', () => {
    window.location.href = 'http://localhost/';
    expect(isDemoMode()).toBe(false);
  });

  it('is true when built with VITE_DEMO=true', () => {
    vi.stubEnv('VITE_DEMO', 'true');
    expect(isDemoMode()).toBe(true);
  });
});

describe('freezeClock', () => {
  const RealDate = globalThis.Date;
  afterEach(() => { globalThis.Date = RealDate; });

  it('pins Date.now() and the no-arg Date constructor to FROZEN_NOW', () => {
    freezeClock();
    expect(Date.now()).toBe(FROZEN_NOW);
    expect(new Date().getTime()).toBe(FROZEN_NOW);
  });

  it('preserves parameterized Date construction', () => {
    freezeClock();
    expect(new Date(0).getTime()).toBe(0);
    expect(new Date('2020-06-15T00:00:00Z').getUTCFullYear()).toBe(2020);
  });
});
