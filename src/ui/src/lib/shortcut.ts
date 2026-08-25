/**
 * Mac renders ⌘K; everything else Ctrl K. Showing a ⌘ to a Windows user names a
 * key their keyboard doesn't have, and this dashboard is mostly run on Windows.
 */
export function isMacPlatform(): boolean {
  return /mac/i.test(navigator.platform || navigator.userAgent);
}

export function shortcutHint(): string {
  return isMacPlatform() ? '⌘K' : 'Ctrl K';
}

/** True when the event is the command palette's open shortcut on this platform. */
export function isPaletteShortcut(e: Pick<KeyboardEvent, 'key' | 'metaKey' | 'ctrlKey'>): boolean {
  if (e.key !== 'k' && e.key !== 'K') {
    return false;
  }

  return isMacPlatform() ? e.metaKey : e.ctrlKey;
}
