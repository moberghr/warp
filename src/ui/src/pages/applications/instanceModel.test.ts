import { describe, expect, it } from 'vitest';
import { clockSkewLabel } from './instanceModel';

describe('clockSkewLabel', () => {
  it('is null within the threshold or when unknown', () => {
    expect(clockSkewLabel(null)).toBeNull();
    expect(clockSkewLabel(undefined)).toBeNull();
    expect(clockSkewLabel(-1000)).toBeNull();
    expect(clockSkewLabel(800)).toBeNull();
  });

  it('reads a negative offset as the server clock ahead', () => {
    expect(clockSkewLabel(-3200)).toBe('clock 3.2 s ahead');
  });

  it('reads a positive offset as the server clock behind, in minutes past one', () => {
    expect(clockSkewLabel(90_000)).toBe('clock 1.5 min behind');
    expect(clockSkewLabel(3 * 3_600_000)).toBe('clock 180 min behind');
  });
});
