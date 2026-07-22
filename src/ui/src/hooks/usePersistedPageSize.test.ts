import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { usePersistedPageSize } from './usePersistedPageSize';

describe('usePersistedPageSize', () => {
  beforeEach(() => localStorage.clear());

  it('defaults to 20 when nothing is stored', () => {
    const { result } = renderHook(() => usePersistedPageSize());
    expect(result.current[0]).toBe(20);
  });

  it('reads a valid stored size', () => {
    localStorage.setItem('warp:pageSize', '50');
    const { result } = renderHook(() => usePersistedPageSize());
    expect(result.current[0]).toBe(50);
  });

  it('ignores an out-of-allowlist stored size', () => {
    localStorage.setItem('warp:pageSize', '999');
    const { result } = renderHook(() => usePersistedPageSize());
    expect(result.current[0]).toBe(20);
  });

  it('persists updates to localStorage', () => {
    const { result } = renderHook(() => usePersistedPageSize());
    act(() => result.current[1](100));
    expect(result.current[0]).toBe(100);
    expect(localStorage.getItem('warp:pageSize')).toBe('100');
  });
});
