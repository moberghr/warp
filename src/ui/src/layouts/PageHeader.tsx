import { useLocation } from 'react-router-dom';
import { usePageStore } from '@/stores/page';

// Routes whose pages render their own bespoke headers (so the shared
// store-driven header should stay out of the way).
const SUPPRESS_EXACT = new Set<string>();
const SUPPRESS_PREFIXES = ['/detail/', '/jobs/detail/', '/messages/detail/', '/batches/detail/'];

export default function PageHeader() {
  const title = usePageStore((s) => s.title);
  const subtitle = usePageStore((s) => s.subtitle);
  const right = usePageStore((s) => s.right);
  const location = useLocation();

  if (SUPPRESS_EXACT.has(location.pathname)) {
    return null;
  }
  if (SUPPRESS_PREFIXES.some((p) => location.pathname.startsWith(p))) {
    return null;
  }
  if (!title || title === 'Warp') {
    return null;
  }

  return (
    <div
      className="flex items-end justify-between gap-4"
      style={{
        padding: '18px 0 14px',
        borderBottom: '1px solid var(--hair)',
      }}
    >
      <div className="min-w-0">
        {subtitle && (
          <div
            className="soft-eyebrow"
            style={{ color: 'var(--brand)' }}
          >
            {subtitle}
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
          {title}
        </h1>
      </div>
      {right && <div className="flex items-center gap-2 shrink-0">{right}</div>}
    </div>
  );
}
