import axios from 'axios';
import type { AxiosResponse } from 'axios';
import { config } from '@/config';

const apiPath = config.apiPath;

const api = axios.create({
  baseURL: apiPath,
});

// Global 401 handler — triggers login screen
let onUnauthorized: (() => void) | null = null;

export function setOnUnauthorized(handler: () => void) {
  onUnauthorized = handler;
}

// Without the built-in login there is no login screen to show: the host gates the dashboard with its own
// policy, so a session that expired mid-visit can only be recovered by a full navigation, which the host's
// authorization then challenges (an OIDC redirect, say). An XHR cannot usefully follow that — it either
// 401s, or the host's scheme redirects it and axios resolves with the identity provider's sign-in HTML
// instead of JSON. Both mean the same thing here. Reloading turns it back into a navigation; the guard
// stops a reload loop when the dashboard is genuinely forbidden rather than merely signed out.
const RELOAD_GUARD_KEY = 'warp.reauth.reloaded';

function reauthenticateViaNavigation() {
  if (sessionStorage.getItem(RELOAD_GUARD_KEY)) {
    return;
  }

  sessionStorage.setItem(RELOAD_GUARD_KEY, '1');
  window.location.reload();
}

function isHtml(response: AxiosResponse | undefined) {
  return String(response?.headers?.['content-type'] ?? '').includes('text/html');
}

api.interceptors.response.use(
  (response: AxiosResponse) => {
    // A 200 carrying HTML from an endpoint that only ever returns JSON is a followed sign-in redirect.
    if (!config.hasBuiltInLogin && isHtml(response)) {
      reauthenticateViaNavigation();
    } else {
      // A genuine response proves the session is alive, so re-arm the guard for the next expiry.
      sessionStorage.removeItem(RELOAD_GUARD_KEY);
    }

    return response;
  },
  error => {
    if (error.response?.status === 401) {
      if (config.hasBuiltInLogin) {
        onUnauthorized?.();
      } else {
        reauthenticateViaNavigation();
      }
    }

    return Promise.reject(error);
  },
);

export default api;
