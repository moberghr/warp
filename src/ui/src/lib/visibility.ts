/**
 * Page-visibility helpers for the dashboard's background work. A tab left open in the background
 * should not keep querying the server: polls and realtime-driven refetches skip while it is
 * hidden and catch up once when it is shown again.
 */

/** True while the tab is hidden (backgrounded, minimised, or another tab is in front). */
export function isTabHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/** Calls `callback` each time the tab becomes visible. Returns the unsubscribe function. */
export function onTabVisible(callback: () => void): () => void {
  const listener = () => {
    if (document.visibilityState === 'visible') callback();
  };
  document.addEventListener('visibilitychange', listener);

  return () => document.removeEventListener('visibilitychange', listener);
}
