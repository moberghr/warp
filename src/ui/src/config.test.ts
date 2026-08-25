import { afterEach, describe, it, expect, vi } from 'vitest';
import { safeUrl } from './config';

describe('safeUrl (branding URL guard)', () => {
  it('allows http(s) URLs', () => {
    expect(safeUrl('https://portal.example.com')).toBe('https://portal.example.com');
    expect(safeUrl('http://internal/app')).toBe('http://internal/app');
  });

  it('allows root-relative URLs', () => {
    expect(safeUrl('/dashboard')).toBe('/dashboard');
  });

  it('is scheme case-insensitive', () => {
    expect(safeUrl('HTTPS://X')).toBe('HTTPS://X');
  });

  it('blocks javascript: and data: (React does not scheme-sanitize href/src)', () => {
    expect(safeUrl('javascript:alert(1)')).toBeNull();
    expect(safeUrl('data:text/html,<script>alert(1)</script>')).toBeNull();
    expect(safeUrl('vbscript:msgbox(1)')).toBeNull();
  });

  it('blocks other schemes and relative-without-slash', () => {
    expect(safeUrl('ftp://x')).toBeNull();
    expect(safeUrl('portal.example.com')).toBeNull();
  });

  it('treats empty / nullish as no URL', () => {
    expect(safeUrl('')).toBeNull();
    expect(safeUrl(null)).toBeNull();
    expect(safeUrl(undefined)).toBeNull();
  });
});

describe('brandName (host-supplied product name)', () => {
  afterEach(() => {
    delete window.warpBrandName;
    vi.resetModules();
  });

  async function loadConfig() {
    vi.resetModules();

    return (await import('./config')).config;
  }

  it('defaults to Warp when the host set nothing', async () => {
    expect((await loadConfig()).brandName).toBe('Warp');
  });

  it('uses the host value when set', async () => {
    window.warpBrandName = 'Acme Jobs';

    expect((await loadConfig()).brandName).toBe('Acme Jobs');
  });

  it('falls back rather than rendering a blank wordmark for an empty or whitespace value', async () => {
    window.warpBrandName = '   ';

    expect((await loadConfig()).brandName).toBe('Warp');
  });

  it('falls back when the host explicitly injected null', async () => {
    window.warpBrandName = null;

    expect((await loadConfig()).brandName).toBe('Warp');
  });
});
