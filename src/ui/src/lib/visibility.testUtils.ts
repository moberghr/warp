/** Test helper: sets document.visibilityState and fires the change event the way a browser does. */
export function setTabVisibility(state: DocumentVisibilityState) {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}
