import api from '@/api/client';
import { config } from '@/config';
import { extensionRuntime } from './runtime';
import { initSDK } from './sdk';
import type { ExtensionManifest, ExtensionModule } from './types';

/**
 * Fetches extension manifests from the API, dynamically imports each
 * extension's JS module, and calls its install() function.
 * Returns the list of manifests (including page registrations) for route setup.
 */
export async function loadExtensions(): Promise<ExtensionManifest[]> {
  // Expose shared deps before any extension code runs
  initSDK();

  let manifests: ExtensionManifest[];
  try {
    const response = await api.get<ExtensionManifest[]>('/extensions');
    manifests = response.data;
  } catch {
    // Extensions are optional — if the API fails, continue without them
    return [];
  }

  // Guard against a 200 with the wrong body (e.g. an SPA-fallback index.html when
  // the endpoint is misrouted) — treat anything that isn't a manifest array as "none"
  // rather than letting a non-array crash the nav (extensions.flatMap downstream).
  if (!Array.isArray(manifests) || manifests.length === 0) {
    return [];
  }

  const extensionAPI = extensionRuntime.createAPI();

  for (const manifest of manifests) {
    if (!manifest.scriptUrl) {
      continue;
    }

    try {
      // Prepend basePath since scriptUrl is relative to the Warp route prefix.
      // Use fetch + blob URL to load the module — this avoids Vite's transform
      // pipeline intercepting the dynamic import() during development.
      const url = config.basePath + manifest.scriptUrl;
      const response = await fetch(url);
      const text = await response.text();
      const blob = new Blob([text], { type: 'application/javascript' });
      const blobUrl = URL.createObjectURL(blob);
      const module: ExtensionModule = await import(/* @vite-ignore */ blobUrl);
      URL.revokeObjectURL(blobUrl);
      module.install(extensionAPI);
    } catch (err) {
      console.error(`[Warp] Failed to load extension "${manifest.name}":`, err);
    }
  }

  // Start the DOM observer after all extensions have registered
  extensionRuntime.start();

  return manifests;
}
