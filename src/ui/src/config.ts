declare global {
  interface Window {
    apiPath?: string;
    basePath?: string;
    hasBuiltInLogin?: boolean;
    warpInstanceName?: string | null;
    warpPortalUrl?: string | null;
    warpPortalLabel?: string | null;
    warpLogoUrl?: string | null;
  }
}

// Only allow http(s) or root-relative URLs for branding hrefs/srcs — React does not scheme-sanitize
// href/src, so this blocks a javascript:/data: value slipping through from a misconfigured WarpUIOptions.
function safeUrl(url: string | null | undefined): string | null {
  return url && /^(https?:\/\/|\/)/i.test(url) ? url : null;
}

export const config = {
  apiPath: window.apiPath || '/warp/api/',
  basePath: window.basePath || '/warp',
  hasBuiltInLogin: window.hasBuiltInLogin === true,
  // Host-configurable branding (WarpUIOptions → injected window globals). All optional.
  instanceName: window.warpInstanceName || null,
  portalUrl: safeUrl(window.warpPortalUrl),
  portalLabel: window.warpPortalLabel || 'Back to app',
  logoUrl: safeUrl(window.warpLogoUrl),
};
