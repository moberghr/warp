import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Search } from 'lucide-react';
import { filterNavTargets, type NavTarget } from '@/layouts/navModel';

/**
 * Search across every nav destination. Grouping the top nav put 16 of the 18
 * pages behind a dropdown, so this is the fast path back to them — type three
 * letters and hit Enter instead of remembering which group owns a page.
 */
export function CommandPalette({
  open,
  targets,
  onClose,
}: {
  open: boolean;
  targets: NavTarget[];
  onClose: () => void;
}) {
  const navigate = useNavigate();
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const results = useMemo(() => filterNavTargets(targets, query), [targets, query]);

  // Every open starts from a clean slate — a stale query from last time reads as
  // a broken palette.
  useEffect(() => {
    if (open) {
      setQuery('');
      setSelected(0);
    }
  }, [open]);

  useEffect(() => { setSelected(0); }, [query]);

  useEffect(() => {
    listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
  }, [selected]);

  if (!open) {
    return null;
  }

  const go = (target: NavTarget) => {
    onClose();
    navigate(target.item.to);
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      onClose();

      return;
    }

    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (results.length === 0) {
        return;
      }

      const step = e.key === 'ArrowDown' ? 1 : -1;
      setSelected((i) => (i + step + results.length) % results.length);

      return;
    }

    if (e.key === 'Enter' && results[selected]) {
      e.preventDefault();
      go(results[selected]);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[12vh]"
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Search pages"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
        className="w-full max-w-lg overflow-hidden rounded-xl bg-popover ring-1 ring-foreground/10 shadow-lg"
      >
        <div className="flex items-center gap-2.5 border-b border-border px-3.5">
          <Search className="h-4 w-4 text-muted-foreground shrink-0" />
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search pages…"
            aria-label="Search pages"
            aria-activedescendant={results[selected] ? `palette-${results[selected].item.to}` : undefined}
            className="flex-1 bg-transparent py-3 text-sm outline-none placeholder:text-muted-foreground"
          />
        </div>
        <div ref={listRef} role="listbox" aria-label="Pages" className="max-h-80 overflow-y-auto p-1.5">
          {results.length === 0 && (
            <p className="px-2.5 py-6 text-center text-sm text-muted-foreground">No matching page</p>
          )}
          {results.map((target, i) => {
            const Icon = target.item.icon;

            return (
              <button
                key={target.item.to}
                id={`palette-${target.item.to}`}
                type="button"
                role="option"
                aria-selected={i === selected}
                onMouseMove={() => setSelected(i)}
                onClick={() => go(target)}
                className={`flex w-full items-center gap-2.5 rounded-md p-2.5 text-left text-sm transition-colors ${
                  i === selected ? 'bg-accent' : ''
                }`}
              >
                <Icon className="h-4 w-4 text-muted-foreground shrink-0" />
                <span className="font-medium">{target.item.label}</span>
                {target.group && <span className="text-xs text-muted-foreground">{target.group}</span>}
                {target.item.hint && (
                  <span className="ml-auto truncate text-xs text-muted-foreground">{target.item.hint}</span>
                )}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
