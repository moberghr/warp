import { afterEach, describe, it, expect, vi } from 'vitest';
import { parseMenuLayout, safeUrl } from './config';

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

describe('parseMenuLayout (host-supplied nav layout)', () => {
  it('reads an ordered layout of pages, groups and dividers', () => {
    const spec = parseMenuLayout({
      entries: [
        { kind: 'page', page: 'dashboard' },
        { kind: 'group', label: 'Ops', pages: ['issues', 'recurring'] },
        { kind: 'divider' },
      ],
      overflowLabel: 'Everything else',
    });

    expect(spec).toEqual({
      entries: [
        { kind: 'page', page: 'dashboard' },
        { kind: 'group', label: 'Ops', pages: ['issues', 'recurring'] },
        { kind: 'divider' },
      ],
      overflowLabel: 'Everything else',
    });
  });

  it('defaults a missing or blank overflow label', () => {
    const entries = [{ kind: 'page', page: 'jobs' }];

    expect(parseMenuLayout({ entries })!.overflowLabel).toBe('More');
    expect(parseMenuLayout({ entries, overflowLabel: '  ' })!.overflowLabel).toBe('More');
    expect(parseMenuLayout({ entries, overflowLabel: 7 })!.overflowLabel).toBe('More');
  });

  it('reads no layout at all as null, so the SPA keeps its built-in nav', () => {
    // The host declaring nothing is the common case — it injects a literal null.
    expect(parseMenuLayout(null)).toBeNull();
    expect(parseMenuLayout(undefined)).toBeNull();
    expect(parseMenuLayout('menu')).toBeNull();
    expect(parseMenuLayout({})).toBeNull();
    expect(parseMenuLayout({ entries: 'nope' })).toBeNull();
    expect(parseMenuLayout({ entries: [] })).toBeNull();
  });

  it('drops malformed entries rather than throwing on the first render', () => {
    const spec = parseMenuLayout({
      entries: [
        null,
        'divider',
        { kind: 'page' },
        { kind: 'page', page: 12 },
        { kind: 'group', label: 'Ops' },
        { kind: 'group', pages: ['issues'] },
        { kind: 'nonsense' },
        { kind: 'page', page: 'jobs' },
      ],
    });

    expect(spec!.entries).toEqual([{ kind: 'page', page: 'jobs' }]);
  });

  it('keeps only the string page ids inside a group', () => {
    const spec = parseMenuLayout({
      entries: [{ kind: 'group', label: 'Ops', pages: ['issues', 3, null, 'recurring'] }],
    });

    expect(spec!.entries).toEqual([{ kind: 'group', label: 'Ops', pages: ['issues', 'recurring'] }]);
  });

  it('degrades to the built-in nav when every entry was malformed', () => {
    expect(parseMenuLayout({ entries: [{ kind: 'nonsense' }] })).toBeNull();
  });
});
