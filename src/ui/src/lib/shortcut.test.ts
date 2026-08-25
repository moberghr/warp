import { afterEach, describe, expect, it, vi } from 'vitest';
import { isPaletteShortcut, shortcutHint } from './shortcut';

function platform(value: string) {
  vi.spyOn(navigator, 'platform', 'get').mockReturnValue(value);
}

afterEach(() => { vi.restoreAllMocks(); });

describe('shortcutHint', () => {
  it('names the Command key on a Mac', () => {
    platform('MacIntel');

    expect(shortcutHint()).toBe('⌘K');
  });

  it('names a key a Windows keyboard actually has', () => {
    platform('Win32');

    expect(shortcutHint()).toBe('Ctrl K');
  });
});

describe('isPaletteShortcut', () => {
  it('takes Ctrl+K off a Mac', () => {
    platform('Win32');

    expect(isPaletteShortcut({ key: 'k', ctrlKey: true, metaKey: false })).toBe(true);
  });

  it('ignores Meta+K off a Mac, where it is the browser shortcut', () => {
    platform('Win32');

    expect(isPaletteShortcut({ key: 'k', ctrlKey: false, metaKey: true })).toBe(false);
  });

  it('takes Meta+K on a Mac', () => {
    platform('MacIntel');

    expect(isPaletteShortcut({ key: 'k', ctrlKey: false, metaKey: true })).toBe(true);
  });

  it('ignores an unmodified k so typing never opens it', () => {
    platform('Win32');

    expect(isPaletteShortcut({ key: 'k', ctrlKey: false, metaKey: false })).toBe(false);
  });

  it('accepts a capital K, which is what Shift or caps lock delivers', () => {
    platform('Win32');

    expect(isPaletteShortcut({ key: 'K', ctrlKey: true, metaKey: false })).toBe(true);
  });

  it('ignores other keys', () => {
    platform('Win32');

    expect(isPaletteShortcut({ key: 'j', ctrlKey: true, metaKey: false })).toBe(false);
  });
});
