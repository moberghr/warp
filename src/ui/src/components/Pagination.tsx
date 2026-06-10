import { PAGE_SIZES } from '@/hooks/usePersistedPageSize';

interface PaginationProps {
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
  pageSize?: number;
  onPageSizeChange?: (size: number) => void;
  /** When provided (with pageSize), the footer shows "Showing x–y of N". */
  totalCount?: number;
  /** Container spacing override — in-panel usages pass their own padding. */
  className?: string;
}

export function Pagination({
  page,
  pageCount,
  onPageChange,
  pageSize,
  onPageSizeChange,
  totalCount,
  className = 'mt-3',
}: PaginationProps) {
  if (pageCount <= 0 && !onPageSizeChange) return null;

  const hasRange = totalCount != null && pageSize != null;
  const showingFrom = hasRange ? (totalCount === 0 ? 0 : page * pageSize + 1) : 0;
  const showingTo = hasRange ? Math.min(totalCount, (page + 1) * pageSize) : 0;

  return (
    <div className={`flex items-center justify-between text-[12px] text-text-mute gap-3 flex-wrap ${className}`}>
      <span className="mono">
        {hasRange ? (
          <>Showing {showingFrom}&ndash;{showingTo} of {totalCount.toLocaleString()}</>
        ) : (
          <>&nbsp;</>
        )}
      </span>
      <div className="flex items-center gap-3">
        {pageSize != null && onPageSizeChange && (
          <label className="flex items-center gap-1.5 text-[11.5px]">
            <span>Per page</span>
            <select
              value={pageSize}
              onChange={(e) => onPageSizeChange(Number(e.target.value))}
              className="px-1.5 py-0.5 text-[11.5px] rounded-md border border-border bg-panel text-foreground"
            >
              {PAGE_SIZES.map((s) => (
                <option key={s} value={s}>{s}</option>
              ))}
            </select>
          </label>
        )}
        <span className="mono">
          Page {pageCount === 0 ? 0 : page + 1} of {pageCount}
        </span>
        <div className="flex gap-1.5">
          <button
            type="button"
            onClick={() => onPageChange(page - 1)}
            disabled={page === 0}
            className="px-2.5 py-1 text-[11.5px] rounded-md border border-border bg-panel text-text-dim hover:bg-panel-2 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            &lsaquo; Prev
          </button>
          <button
            type="button"
            onClick={() => onPageChange(page + 1)}
            disabled={page >= pageCount - 1}
            className="px-2.5 py-1 text-[11.5px] rounded-md border border-border bg-panel text-foreground hover:bg-panel-2 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            Next &rsaquo;
          </button>
        </div>
      </div>
    </div>
  );
}
