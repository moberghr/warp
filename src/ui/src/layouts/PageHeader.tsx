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
  const right = usePageStore((s) => s.right);
  const location = useLocation();
  const suppressed = isSuppressed(location.pathname);

  // Hold the last real title so the header stays put during the brief
  // unmount→remount window when navigating between pages (outgoing page's
  // cleanup resets the store to 'Warp', incoming page sets the new title a
  // tick later). Mirror the store as soon as a real title arrives — do NOT
  // clear on pathname change, otherwise navigating within the same component
  // (e.g. /jobs/enqueued → /jobs/completed) causes a visible flicker.
  const [displayed, setDisplayed] = useState({ title, right });
  const lastTitle = useRef(title);

  useEffect(() => {
    if (title && title !== 'Warp') {
      setDisplayed({ title, right });
      lastTitle.current = title;
    }
  }, [title, right]);

  if (suppressed) return null;
  const t = displayed.title;
  if (!t || t === 'Warp') return null;

  return (
    <div
      className="flex items-end justify-between gap-4"
      style={{
        padding: '16px 0 12px',
        borderBottom: '1px solid var(--hair)',
      }}
    >
      <h1
        className="m-0 min-w-0 font-semibold text-foreground truncate"
        style={{
          fontSize: 30,
          letterSpacing: '-0.8px',
          lineHeight: 1,
        }}
      >
        {t}
      </h1>
      {displayed.right && (
        <div className="flex items-center gap-2 shrink-0">{displayed.right}</div>
      )}
    </div>
  );
}
