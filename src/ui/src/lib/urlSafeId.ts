/**
 * URL-safe base64 codec for a string identity travelling as one route segment — mirrors the backend
 * Warp.Core.Models.UrlSafeId (base64 of the UTF-8 bytes, '+'→'-', '/'→'_', trailing '=' trimmed).
 *
 * Used wherever the dashboard addresses something by an arbitrary name rather than a numeric id:
 * endpoint routes, application names, and recurring job names (which may hold '/' and spaces).
 */
export function encodeUrlSafeId(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = '';
  for (const b of bytes) {
    binary += String.fromCharCode(b);
  }

  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** Inverse of encodeUrlSafeId: restores the padding and the two substitutions. */
export function decodeUrlSafeId(id: string): string {
  const b64 = id.replace(/-/g, '+').replace(/_/g, '/');
  const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);

  return new TextDecoder().decode(Uint8Array.from(atob(padded), (c) => c.charCodeAt(0)));
}
