import { describe, it, expect, afterEach, vi } from 'vitest';
import { decideReauth, reloadGuard } from './reauth';

const page = { hasBuiltInLogin: false, isWarpApiResponse: true, responseless: false };

describe('decideReauth', () => {
  it('shows the SPA login page on 401 when the built-in login is enabled', () => {
    expect(decideReauth({ ...page, hasBuiltInLogin: true, status: 401 })).toBe('login-page');
  });

  it('navigates on 401 when the host owns authentication', () => {
    expect(decideReauth({ ...page, status: 401 })).toBe('navigate');
  });

  it('does nothing on 403 — the caller is forbidden, not signed out', () => {
    // Reloading here would bounce forever: a navigation re-challenge returns the same verdict.
    expect(decideReauth({ ...page, status: 403 })).toBe('none');
  });

  it('navigates when a 200 did not come from the Warp API (a followed sign-in redirect)', () => {
    expect(decideReauth({ ...page, status: 200, isWarpApiResponse: false })).toBe('navigate');
  });

  it('does nothing when a 200 carries the Warp API marker, whatever its content type', () => {
    // A host extension endpoint is free to return HTML; it is still the Warp API answering.
    expect(decideReauth({ ...page, status: 200, isWarpApiResponse: true })).toBe('none');
  });

  it('navigates when the request failed with no response at all', () => {
    // A cross-origin challenge redirect is killed by CORS and surfaces with no response.
    expect(decideReauth({ ...page, responseless: true })).toBe('navigate');
  });

  it('does nothing for a response-less failure under the built-in login', () => {
    expect(decideReauth({ ...page, hasBuiltInLogin: true, responseless: true })).toBe('none');
  });

  it('does nothing for an ordinary server error', () => {
    expect(decideReauth({ ...page, status: 500 })).toBe('none');
  });
});

describe('reloadGuard', () => {
  afterEach(() => {
    reloadGuard.clear();
    vi.unstubAllGlobals();
  });

  it('permits the first reload and blocks the second', () => {
    expect(reloadGuard.tryClaim()).toBe(true);
    expect(reloadGuard.tryClaim()).toBe(false);
  });

  it('permits a reload again once cleared by a genuine response', () => {
    reloadGuard.tryClaim();
    reloadGuard.clear();

    expect(reloadGuard.tryClaim()).toBe(true);
  });

  it('survives a browser that blocks DOM storage', () => {
    // Blocked storage throws SecurityError on access. Session recovery is a nicety — it must never
    // turn a working dashboard into an error state.
    vi.stubGlobal('sessionStorage', {
      getItem: () => {
        throw new Error('SecurityError');
      },
      setItem: () => {
        throw new Error('SecurityError');
      },
      removeItem: () => {
        throw new Error('SecurityError');
      },
    });

    expect(() => reloadGuard.clear()).not.toThrow();
    expect(() => reloadGuard.tryClaim()).not.toThrow();
    // Without storage there is no way to remember a previous reload, so it must refuse to loop.
    expect(reloadGuard.tryClaim()).toBe(false);
  });
});
