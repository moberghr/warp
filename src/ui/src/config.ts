import type { MenuEntrySpec, MenuLayoutSpec } from '@/layouts/navModel';

declare global {
  interface Window {
    apiPath?: string;
    basePath?: string;
    hasBuiltInLogin?: boolean;
    warpBrandName?: string | null;
    warpInstanceName?: string | null;
    warpPortalUrl?: string | null;
    warpPortalLabel?: string | null;
    warpLogoUrl?: string | null;
    warpMenu?: unknown;
  }
}

/**
 * The host's nav layout, injected by MapWarpDashboard as a JSON object (or null when it declared none).
 * Shape-checked rather than cast: it is host config, but a malformed value must degrade to the built-in
 * nav instead of throwing on the first render. Unknown page ids are dropped later, by applyMenuLayout.
 */
export function parseMenuLayout(value: unknown): MenuLayoutSpec | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const raw = value as { entries?: unknown; overflowLabel?: unknown };
  if (!Array.isArray(raw.entries)) {
    return null;
  }

  const entries = raw.entries.flatMap((entry): MenuEntrySpec[] => {
    if (!entry || typeof entry !== 'object') {
      return [];
    }

    const x = entry as { kind?: unknown; page?: unknown; label?: unknown; pages?: unknown };
    if (x.kind === 'divider') {
      return [{ kind: 'divider' }];
    }

    if (x.kind === 'page' && typeof x.page === 'string') {
      return [{ kind: 'page', page: x.page }];
    }

    if (x.kind === 'group' && typeof x.label === 'string' && Array.isArray(x.pages)) {
      return [{ kind: 'group', label: x.label, pages: x.pages.filter((y): y is string => typeof y === 'string') }];
    }

    return [];
  });

  if (entries.length === 0) {
    return null;
  }

  return {
    entries,
    overflowLabel:
      typeof raw.overflowLabel === 'string' && raw.overflowLabel.trim() ? raw.overflowLabel : 'More',
  };
}

// Only allow http(s) or root-relative URLs for branding hrefs/srcs — React does not scheme-sanitize
// href/src, so this blocks a javascript:/data: value slipping through from a misconfigured WarpDashboardOptions.
export function safeUrl(url: string | null | undefined): string | null {
  return url && /^(https?:\/\/|\/)/i.test(url) ? url : null;
}

export const config = {
  apiPath: window.apiPath || '/warp/api/',
  basePath: window.basePath || '/warp',
  hasBuiltInLogin: window.hasBuiltInLogin === true,
  // Host-configurable branding (WarpDashboardOptions → injected window globals). All optional.
  // brandName is the only one with a non-null fallback — something has to name the
  // wordmark and the tab, and an empty string from a misconfigured host must not
  // blank them out.
  brandName: window.warpBrandName?.trim() || 'Warp',
  instanceName: window.warpInstanceName || null,
  portalUrl: safeUrl(window.warpPortalUrl),
  portalLabel: window.warpPortalLabel || 'Back to app',
  logoUrl: safeUrl(window.warpLogoUrl),
  // null = the host declared no layout, so the nav keeps its built-in groups and order.
  menu: parseMenuLayout(window.warpMenu),
};
