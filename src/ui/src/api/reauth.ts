/**
 * Deciding what an unauthenticated-looking API response means.
 *
 * When the host owns authentication there is no login form for the SPA to show: the only way back in
 * is a full navigation, which the host's authorization then challenges (an OIDC redirect, say). An XHR
 * cannot usefully follow that — it either 401s, gets redirected and resolves with someone else's HTML,
 * or is killed by CORS and resolves with no response at all. All three mean "sign in again".
 */
export type ReauthSignal = 'none' | 'login-page' | 'navigate';

export interface ReauthInputs {
  hasBuiltInLogin: boolean;
  /** HTTP status, or undefined when the request produced no response. */
  status?: number;
  /**
   * Whether the response carried Warp's API marker header. A 2xx without it did not come from the
   * Warp API, which means something intercepted the call — the precise signal a content-type sniff
   * only approximated, and one that never misfires on an extension endpoint returning HTML.
   */
  isWarpApiResponse: boolean;
  /** The request failed with no response: network error, or a CORS-blocked challenge redirect. */
  responseless: boolean;
}

export function decideReauth({ hasBuiltInLogin, status, isWarpApiResponse, responseless }: ReauthInputs): ReauthSignal {
  if (status === 401) {
    return hasBuiltInLogin ? 'login-page' : 'navigate';
  }

  // 403 is deliberately absent: the caller is authenticated and forbidden, so a re-challenge returns
  // the same answer. Reloading would loop.
  if (hasBuiltInLogin) {
    return 'none';
  }

  if (responseless) {
    return 'navigate';
  }

  if (status !== undefined && status >= 200 && status < 300 && !isWarpApiResponse) {
    return 'navigate';
  }

  return 'none';
}

const RELOAD_GUARD_KEY = 'warp.reauth.reloaded';

// sessionStorage throws on access when the browser blocks DOM storage (strict cookie settings, some
// embedded WebViews). Session recovery is a convenience; it must never break a working dashboard.
function withStorage<T>(action: (storage: Storage) => T, fallback: T): T {
  try {
    return action(sessionStorage);
  } catch {
    return fallback;
  }
}

export const reloadGuard = {
  /**
   * Claims the single reload this session is allowed. Returns false if one was already spent — or if
   * storage is unavailable, since without somewhere to record the attempt there is no way to stop a
   * reload loop, and looping is worse than not recovering.
   */
  tryClaim(): boolean {
    return withStorage(storage => {
      if (storage.getItem(RELOAD_GUARD_KEY) !== null) {
        return false;
      }

      storage.setItem(RELOAD_GUARD_KEY, '1');

      return true;
    }, false);
  },

  /** Re-arms the guard. Called on a genuine API response, which proves the session is alive. */
  clear(): void {
    withStorage(storage => storage.removeItem(RELOAD_GUARD_KEY), undefined);
  },
};
