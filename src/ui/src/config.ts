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

export const config = {
  apiPath: window.apiPath || '/warp/api/',
  basePath: window.basePath || '/warp',
  hasBuiltInLogin: window.hasBuiltInLogin === true,
  // Host-configurable branding (WarpUIOptions → injected window globals). All optional.
  instanceName: window.warpInstanceName || null,
  portalUrl: window.warpPortalUrl || null,
  portalLabel: window.warpPortalLabel || 'Back to app',
  logoUrl: window.warpLogoUrl || null,
};
