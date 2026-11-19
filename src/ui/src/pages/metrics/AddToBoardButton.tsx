// "Add to board" (spec 2026-11-19): on the metric detail page it saves the chart as it is set up now (the URL's controls,
// the range left out) as a chart tile, named after the instrument by default (batch 1); on the SLO detail page it saves
// an SLO tile showing the objective, named after it by default (batch 2). Either goes on a picked board.
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import * as api from '@/api';
import { errorMessage } from '@/lib/apiError';
import { inputClass } from '@/pages/slo/shared';
import type { MetricBoardSummary } from '@/types/metricBoard';
import { MAX_TILES } from './boardModel';
import { writeBoardParam } from './boardsModel';
import { addToBoardError, boardTileCounts } from './chartTileModel';
import { addToBoardName, addToBoardTile, type AddToBoardSource } from './sloTileModel';

const buttonClass = 'h-8 rounded-md border border-border px-3 text-sm text-muted-foreground hover:bg-muted';

export function AddToBoardButton({ source }: { source: AddToBoardSource }) {
  const [open, setOpen] = useState(false);
  const [added, setAdded] = useState<MetricBoardSummary | null>(null);

  return (
    <>
      <span className="flex items-center gap-2 text-sm">
        <button type="button" className={buttonClass} onClick={() => setOpen(true)}>Add to board</button>
        {added && <AddedLink board={added} />}
      </span>
      {open && (
        <AddToBoardDialog
          source={source}
          onCancel={() => setOpen(false)}
          onAdded={(board) => {
            setOpen(false);
            setAdded(board);
          }}
        />
      )}
    </>
  );
}

function AddedLink({ board }: { board: MetricBoardSummary }) {
  const boards = useQuery({ queryKey: ['metrics', 'boards'] as const, queryFn: api.getMetricBoards }).data ?? [board];
  const search = writeBoardParam(new URLSearchParams(), boards, board).toString();

  return (
    <span role="status" className="text-xs text-muted-foreground">
      Added to <Link className="text-primary hover:underline" to={`/metrics/board${search ? `?${search}` : ''}`}>{board.name}</Link>.
    </span>
  );
}

function AddToBoardDialog({
  source,
  onCancel,
  onAdded,
}: {
  source: AddToBoardSource;
  onCancel: () => void;
  onAdded: (board: MetricBoardSummary) => void;
}) {
  const queryClient = useQueryClient();
  const boardsQuery = useQuery({ queryKey: ['metrics', 'boards'] as const, queryFn: api.getMetricBoards });
  const tilesQuery = useQuery({ queryKey: ['metrics', 'board', 'tiles'] as const, queryFn: api.getMetricBoardTiles });
  const boards = useMemo(() => boardsQuery.data ?? [], [boardsQuery.data]);
  const [name, setName] = useState(() => addToBoardName(source));
  const [picked, setPicked] = useState<number | null>(null);
  const board = boards.find((x) => x.id === picked) ?? boards.find((x) => x.isDefault) ?? boards[0];
  const counts = useMemo(
    () => (tilesQuery.data ? boardTileCounts(tilesQuery.data, boards.find((x) => x.isDefault)?.id ?? null) : null),
    [tilesQuery.data, boards],
  );
  const boardCounts = board && counts ? (counts.get(board.id) ?? { tileCount: 0, chartCount: 0 }) : null;
  const clientError = board ? addToBoardError(name, boardCounts, MAX_TILES, source.kind === 'chart') : 'Loading the boards…';

  const save = useMutation({
    mutationFn: () => api.upsertMetricBoardTile(addToBoardTile(source, name, board!.id)),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['metrics', 'board'] });
      void queryClient.invalidateQueries({ queryKey: ['metrics', 'boards'] });
      onAdded(board!);
    },
  });

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div role="dialog" aria-label="Add to board" className="w-full max-w-md rounded-xl bg-white p-6 shadow-xl dark:bg-gray-900" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-1 text-lg font-semibold">Add to board</h2>
        <p className="mb-4 text-xs text-muted-foreground">
          {source.kind === 'chart'
            ? 'The chart as it is set up now (breakdown, filters, compare, percentile, view). The board’s range drives it.'
            : 'The objective’s current state, attainment, error budget and burn rates, linking back here.'}
        </p>
        <div className="space-y-3 text-sm">
          <label className="block">
            <span className="mb-1 block text-xs text-gray-500">Board</span>
            <select
              className={inputClass}
              aria-label="Board"
              value={board?.id ?? ''}
              disabled={boards.length === 0}
              onChange={(e) => setPicked(Number(e.target.value))}
            >
              {boards.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
            </select>
          </label>
          <label className="block">
            <span className="mb-1 block text-xs text-gray-500">Tile name</span>
            <input className={inputClass} aria-label="Tile name" value={name} onChange={(e) => setName(e.target.value)} />
          </label>
        </div>
        {boardsQuery.isError && <p role="alert" className="mt-4 text-sm text-red-600">Unable to load the boards.</p>}
        {clientError && board && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
        {save.isError && <p role="alert" className="mt-2 text-sm text-red-600">{errorMessage(save.error, 'Failed to add the tile.')}</p>}
        <div className="mt-6 flex justify-end gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg px-4 py-2 text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button
            type="button"
            onClick={() => save.mutate()}
            disabled={save.isPending || clientError !== null}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {save.isPending ? 'Adding…' : 'Add'}
          </button>
        </div>
      </div>
    </div>
  );
}
