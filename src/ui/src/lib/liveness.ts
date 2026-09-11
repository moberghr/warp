/**
 * Is this process still checking in?
 *
 * Liveness is a fact that DECAYS, which is what makes it awkward to render: the API answers it for the
 * instant it was asked, and the answer rots while the page sits open. A client that cannot see the
 * threshold has only two options — re-ask the server, or guess. The dashboard guessed (30 seconds),
 * the server used `ApplicationInstanceStaleGrace` (two minutes), and the same silent process rendered
 * red on its server page and green on the roster.
 *
 * So the server sends the threshold it used (`WarpAddonsInfo.instanceStaleAfterSeconds`) and every
 * surface re-derives the same answer from it, on the shared clock. One rule, one number, computed in
 * one place — and a dot that turns red the second the grace expires rather than when a poll happens
 * to land.
 */

// What to use before /api/addons answers, and against a pre-6.2 backend that does not send the
// threshold at all. Six missed heartbeats at the default 5s HealthCheckInterval — the dashboard's
// historical guess, kept precisely because it IS the historical behaviour.
export const FALLBACK_STALE_MS = 30_000;

let graceMs: number | null = null;

/**
 * Adopt the server's threshold. Called once from the layout's boot fetch; `null`/`undefined` (an older
 * backend) leaves every surface on the fallback rather than on a number nobody sent.
 */
export function setInstanceStaleGrace(seconds: number | null | undefined): void {
  graceMs = typeof seconds === 'number' && seconds > 0 ? seconds * 1000 : null;
}

/** True once the server's own threshold is in hand — the seam that decides whether to trust `isLive`. */
export function hasServerGrace(): boolean {
  return graceMs !== null;
}

export function staleGraceMs(): number {
  return graceMs ?? FALLBACK_STALE_MS;
}

export function isStale(lastHeartbeatTime: string, now: number = Date.now()): boolean {
  return now - new Date(lastHeartbeatTime).getTime() > staleGraceMs();
}

export function statusDotColor(stale: boolean, pausedAt: string | null): string {
  // Paused outranks stale: a paused server stops checking in on purpose, and reporting that as a
  // fault sends the operator looking for a problem they created.
  if (pausedAt) {
    return 'bg-amber-500';
  }

  return stale ? 'bg-red-500' : 'bg-green-500';
}

/** Test seam — the adopted threshold is module state. */
export function resetLivenessForTests(): void {
  graceMs = null;
}
