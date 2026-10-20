import { Button } from '@/components/ui/button';
import { ChevronLeft, ChevronRight } from 'lucide-react';

interface PaginationProps {
  page: number;
  pageCount: number;
  /** For lists that page without a total: enables "next" and labels the page without "of N". */
  hasNext?: boolean;
  onPageChange: (page: number) => void;
  pageSize?: number;
  onPageSizeChange?: (size: number) => void;
}

export function Pagination({ page, pageCount, hasNext, onPageChange, pageSize, onPageSizeChange }: PaginationProps) {
  const counted = hasNext === undefined;
  if (counted && pageCount <= 0 && !onPageSizeChange) return null;

  return (
    <div className="flex items-center gap-2 justify-center mt-4">
      <Button
        variant="outline"
        size="sm"
        onClick={() => onPageChange(page - 1)}
        disabled={page === 0}
        aria-label="Previous page"
      >
        <ChevronLeft className="h-4 w-4" />
      </Button>
      {counted ? pageCount > 0 && (
        <span className="text-sm text-muted-foreground">
          Page {page + 1} of {pageCount}
        </span>
      ) : (
        <span className="text-sm text-muted-foreground">Page {page + 1}</span>
      )}
      <Button
        variant="outline"
        size="sm"
        onClick={() => onPageChange(page + 1)}
        disabled={counted ? page >= pageCount - 1 : !hasNext}
        aria-label="Next page"
      >
        <ChevronRight className="h-4 w-4" />
      </Button>
      {onPageSizeChange && (
        <select
          value={pageSize}
          onChange={e => onPageSizeChange(Number(e.target.value))}
          className="ml-4 px-2 py-1 text-sm border rounded-md bg-background"
        >
          {[10, 20, 50, 100].map(size => (
            <option key={size} value={size}>{size} per page</option>
          ))}
        </select>
      )}
    </div>
  );
}
