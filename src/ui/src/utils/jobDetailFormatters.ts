/** Format milliseconds into a human-readable duration string. */
export function formatDuration(ms: number | null | undefined): string | null {
  if (ms == null) {
    return null;
  }
  if (ms < 1) {
    return '<1ms';
  }
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }
  if (ms < 60000) {
    return `${(ms / 1000).toFixed(2)}s`;
  }
  const mins = Math.floor(ms / 60000);
  const secs = Math.floor((ms % 60000) / 1000);

  return `${mins}m ${secs}s`;
}

/** Format an ISO timestamp as a relative "X ago" string. */
export function relativeFromNow(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) {
    return `${Math.max(1, Math.floor(diff / 1000))}s ago`;
  }
  if (diff < 3_600_000) {
    return `${Math.floor(diff / 60_000)}m ago`;
  }
  if (diff < 86_400_000) {
    return `${Math.floor(diff / 3_600_000)}h ago`;
  }

  return `${Math.floor(diff / 86_400_000)}d ago`;
}
