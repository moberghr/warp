import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { usePageStore } from '@/stores/page';

// Routes whose pages render their own bespoke headers (so the shared
// store-driven header should stay out of the way).
const SUPPRESS_EXACT = new Set<string>();
const SUPPRESS_PREFIXES = ['/detail/', '/jobs/detail/', '/messages/detail/', '/batches/detail/'];

function isSuppressed(pathname: string) {
  if (SUPPRESS_EXACT.has(pathname)) return true;
  return SUPPRESS_PREFIXES.some((p) => pathname.startsWith(p));
}

export default function PageHeader() {
  const title = usePageStore((s) => s.title);
  const subtitle = usePageStore((s) => s.subtitle);
  const right = usePageStore((s) => s.right);
  const location = useLocation();
  const suppressed = isSuppressed(location.pathname);

  // Hold the last fresh title across the brief unmount→remount window when
  // navigating between pages (outgoing page resets the store to 'Warp' on
  // cleanup, incoming page sets a new title on mount). Without this guard
  // the header collapses and reappears, causing a layout flicker.
  const [displayed, setDisplayed] = useState({ title, subtitle, right });
  const lastPath = useRef(location.pathname);

  useEffect(() => {
    if (location.pathname !== lastPath.current) {
      lastPath.current = location.pathname;
      // Clear the held title; the new page will set its own.
      setDisplayed({ title: '', subtitle: undefined, right: undefined });

      return;
    }
    if (title && title !== 'Warp') {
      setDisplayed({ title, subtitle, right });
    }
  }, [title, subtitle, right, location.pathname]);

  if (suppressed) return null;
  const t = displayed.title;
  if (!t || t === 'Warp') return null;

  return (
    <div
      className="flex items-end justify-between gap-4"
      style={{
        padding: '18px 0 14px',
        borderBottom: '1px solid var(--hair)',
      }}
    >
      <div className="min-w-0">
        {displayed.subtitle && (
          <div className="soft-eyebrow" style={{ color: 'var(--brand)' }}>
            {displayed.subtitle}
          </div>
        )}
        <h1
          className="m-0 mt-2 font-semibold text-foreground truncate"
          style={{
            fontSize: 30,
            letterSpacing: '-0.8px',
            lineHeight: 1,
          }}
        >
          {t}
        </h1>
      </div>
      {displayed.right && (
        <div className="flex items-center gap-2 shrink-0">{displayed.right}</div>
      )}
    </div>
  );
}
