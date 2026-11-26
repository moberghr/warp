import { describe, expect, it } from 'vitest';
import api, { WARP_REQUEST_HEADER } from './client';

describe('api client', () => {
  // The server rejects state-changing calls that lack this header (its CSRF guard), so every request must send it.
  it('sends the Warp request header on every request', () => {
    expect(api.defaults.headers[WARP_REQUEST_HEADER]).toBe('1');
  });
});
