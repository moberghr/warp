import { describe, it, expect } from 'vitest';
import { cn } from './utils';

describe('cn', () => {
  it('joins truthy class values', () => {
    expect(cn('a', 'b')).toBe('a b');
  });

  it('drops falsy values and supports conditionals', () => {
    expect(cn('a', false, null, undefined, 'b', 0 && 'c')).toBe('a b');
  });

  it('merges conflicting tailwind classes (last wins)', () => {
    expect(cn('px-2', 'px-4')).toBe('px-4');
    expect(cn('text-red-500', 'text-blue-500')).toBe('text-blue-500');
  });
});
