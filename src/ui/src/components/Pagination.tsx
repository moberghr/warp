import { ChevronLeft, ChevronRight } from 'lucide-react';

interface PaginationProps {
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
  pageSize?: number;
  onPageSizeChange?: (size: number) => void;
}

export function Pagination({
  page,
  pageCount,
  onPageChange,
  pageSize,
  onPageSizeChange,
}: PaginationProps) {
  if (pageCount <= 0 && !onPageSizeChange) return null;

  const canPrev = page > 0;
  const canNext = page < pageCount - 1;

  return (
    <div className="flex items-center justify-between gap-3 px-3.5 py-3 border-t border-hair">
      <span className="mono text-[11.5px] text-text-mute tabular-nums">
        {pageCount > 0 ? (
          <>
            Page <span className="text-text-dim font-semibold">{page + 1}</span> of{' '}
            <span className="text-text-dim font-semibold">{pageCount}</span>
          </>
        ) : (
          <span>&nbsp;</span>
        )}
      </span>

      <div className="flex items-center gap-2">
        {pageCount > 0 && (
          <>
            <button
              type="button"
              onClick={() => onPageChange(page - 1)}
              disabled={!canPrev}
              aria-label="Previous page"
              className="soft-btn soft-btn-ghost soft-btn-xs"
              style={{ padding: '5px 9px' }}
            >
              <ChevronLeft className="h-3.5 w-3.5" />
            </button>
            <button
              type="button"
              onClick={() => onPageChange(page + 1)}
              disabled={!canNext}
              aria-label="Next page"
              className="soft-btn soft-btn-ghost soft-btn-xs"
              style={{ padding: '5px 9px' }}
            >
              <ChevronRight className="h-3.5 w-3.5" />
            </button>
          </>
        )}

        {onPageSizeChange && (
          <select
            value={pageSize}
            onChange={(e) => onPageSizeChange(Number(e.target.value))}
            className="mono text-[11.5px] rounded-md border bg-white py-1 pl-2 pr-7 text-text-dim hover:bg-paper"
            style={{ borderColor: 'var(--border-hi)' }}
          >
            {[10, 20, 50, 100].map((size) => (
              <option key={size} value={size}>
                {size} / page
              </option>
            ))}
          </select>
        )}
      </div>
    </div>
  );
}
