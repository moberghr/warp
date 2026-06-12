interface BulkActionBarProps {
  count: number;
  total: number;
  activeType: string | null;
  stateLabel: string;
  onRequeue: () => void;
  onDelete: () => void;
  onRequeueAllType?: () => void;
  onDeleteAllType?: () => void;
}

export function BulkActionBar({
  count,
  total,
  activeType,
  stateLabel,
  onRequeue,
  onDelete,
  onRequeueAllType,
  onDeleteAllType,
}: BulkActionBarProps) {
  if (count === 0) {
    return null;
  }

  return (
    <div className="sticky top-0 z-[5] flex items-center justify-between px-3.5 py-2.5 mb-3 rounded-lg bg-warp-green-soft border border-warp-green/40 text-[13px]">
      <div>
        <span className="font-semibold">{count} selected</span>
        <span className="text-text-dim ml-2.5">
          of {total} {activeType ? `· ${activeType}` : `${stateLabel} jobs`}
        </span>
      </div>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={onRequeue}
          className="px-3 py-1 text-[12px] font-medium rounded-md bg-warp-green text-white hover:opacity-90 transition-opacity"
        >
          Requeue {count}
        </button>
        <button
          type="button"
          onClick={onDelete}
          className="px-3 py-1 text-[12px] font-medium rounded-md border border-warp-red text-warp-red hover:bg-warp-red-soft transition-colors"
        >
          Delete {count}
        </button>
        {activeType && onRequeueAllType && (
          <button
            type="button"
            onClick={onRequeueAllType}
            className="px-3 py-1 text-[12px] font-medium rounded-md border border-border bg-panel text-foreground hover:bg-panel-2 transition-colors"
          >
            Requeue all {activeType}
          </button>
        )}
        {activeType && onDeleteAllType && (
          <button
            type="button"
            onClick={onDeleteAllType}
            className="px-3 py-1 text-[12px] font-medium rounded-md border border-warp-red text-warp-red hover:bg-warp-red-soft transition-colors"
          >
            Delete all {activeType}
          </button>
        )}
      </div>
    </div>
  );
}
