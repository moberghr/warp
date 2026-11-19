// An SLO tile on a metric board (spec 2026-11-19 batch 2): the objective's current evaluation from the board read (one
// objective read for the whole board) — state, attainment against target, error budget, fast / slow burn and when it
// was last evaluated — linking to the SLO page. A tile whose objective was deleted says so and offers to remove it.
import { Link } from 'react-router-dom';
import { SloStatePill } from '@/pages/slo/shared';
import { DASHBOARD_LOCALE } from '@/utils/format';
import type { MetricBoardTileResult } from '@/types/metricBoard';
import { tileStatusClass } from './boardModel';
import { sloTileView } from './sloTileModel';

const formatInstant = (iso: string) => new Date(iso).toLocaleString(DASHBOARD_LOCALE);

export function SloTileCard({
  result,
  onEdit,
  onDelete,
}: {
  result: MetricBoardTileResult;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const view = sloTileView(result, formatInstant);
  const { tile } = result;

  if (view.missing) {
    return (
      <div className="rounded-lg border-2 border-dashed border-amber-500/70 bg-card p-3" data-testid="slo-tile">
        <div className="flex items-start justify-between gap-2">
          <span className="truncate text-sm font-medium">{tile.name}</span>
          <span className="inline-block rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-900/40 dark:text-amber-300">
            Missing objective
          </span>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">{view.message}</p>
        <div className="mt-3 flex justify-end gap-3 text-xs">
          <button type="button" className="text-muted-foreground hover:underline" onClick={onEdit}>Edit</button>
          <button type="button" className="text-red-600 hover:underline" onClick={onDelete}>Remove tile</button>
        </div>
      </div>
    );
  }

  return (
    <div className={`rounded-lg border-2 bg-card p-3 ${view.enabled ? tileStatusClass(view.state) : 'border-border'}`} data-testid="slo-tile">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <Link to={view.href} className="block truncate text-sm font-medium hover:underline" title={view.objectiveName}>
            {tile.name}
          </Link>
          <div className="truncate text-[11px] text-muted-foreground">
            SLO · {view.kindLabel}
            {view.application && ` · @${view.application}`}
          </div>
        </div>
        <SloStatePill state={view.state} enabled={view.enabled} />
      </div>

      {view.note ? (
        <p className="mt-3 text-sm text-muted-foreground">{view.note}</p>
      ) : (
        <div className="mt-2 flex items-baseline justify-between gap-2">
          <span className="text-2xl font-semibold tabular-nums">{view.observed}</span>
          <span className="text-xs text-muted-foreground">target {view.target}</span>
        </div>
      )}

      <div className="mt-1 flex flex-wrap gap-4 text-xs text-muted-foreground">
        {view.budget !== null && (
          <span>
            Budget <span className="tabular-nums text-foreground">{view.budget}</span>
          </span>
        )}
        {view.burn !== null && (
          <span>
            Burn <span className={`tabular-nums ${view.burning ? 'text-red-600' : 'text-foreground'}`}>{view.burn}</span>
          </span>
        )}
        {view.note && <span>target {view.target}</span>}
      </div>

      <div className="mt-2 flex items-center justify-between text-xs">
        <span className="flex flex-col text-muted-foreground">
          <span>{view.lastEvaluatedAt ? `Evaluated ${view.lastEvaluatedAt}` : 'Never evaluated'}</span>
          {view.acknowledgedUntil && <span>{view.acknowledgedUntil}</span>}
        </span>
        <span className="flex gap-3">
          <button type="button" className="text-muted-foreground hover:underline" onClick={onEdit}>Edit</button>
          <button type="button" className="text-red-600 hover:underline" onClick={onDelete}>Delete</button>
        </span>
      </div>
    </div>
  );
}
