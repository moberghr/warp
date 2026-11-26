import axios from 'axios';
import type { AxiosResponse } from 'axios';
import { config } from '@/config';
import { decideReauth, reloadGuard, type ReauthSignal } from './reauth';

const apiPath = config.apiPath;

/**
 * Sent on every API request. The server rejects state-changing requests without it, which a cross-site
 * page cannot add without a CORS preflight — the dashboard's CSRF defence.
 */
export const WARP_REQUEST_HEADER = 'X-Warp-Request';

const api = axios.create({
  baseURL: apiPath,
  headers: { [WARP_REQUEST_HEADER]: '1' },
});

// Global 401 handler — triggers login screen
let onUnauthorized: (() => void) | null = null;

export function setOnUnauthorized(handler: () => void) {
  onUnauthorized = handler;
}

/** Stamped on every Warp API response. Its absence on a 2xx means something else answered. */
const WARP_API_HEADER = 'x-warp-api';

function carriesApiMarker(response: AxiosResponse | undefined) {
  return response?.headers?.[WARP_API_HEADER] !== undefined;
}

function act(signal: ReauthSignal) {
  if (signal === 'login-page') {
    onUnauthorized?.();
    return;
  }

  if (signal === 'navigate' && reloadGuard.tryClaim()) {
    // Turns a dead XHR back into a navigation, which the host's authorization can challenge.
    window.location.reload();
  }
}

api.interceptors.response.use(
  (response: AxiosResponse) => {
    const signal = decideReauth({
      hasBuiltInLogin: config.hasBuiltInLogin,
      status: response.status,
      isWarpApiResponse: carriesApiMarker(response),
      responseless: false,
    });

    if (signal === 'none') {
      // A genuine API response proves the session is alive, so re-arm the guard for the next expiry.
      reloadGuard.clear();
    }

    act(signal);

    return response;
  },
  error => {
    act(decideReauth({
      hasBuiltInLogin: config.hasBuiltInLogin,
      status: error.response?.status,
      isWarpApiResponse: carriesApiMarker(error.response),
      responseless: error.response === undefined,
    }));

    return Promise.reject(error);
  },
);

export default api;
