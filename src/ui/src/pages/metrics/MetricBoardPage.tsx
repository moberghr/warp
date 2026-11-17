import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Chart, LineController, LineElement, LinearScale, PointElement, Tooltip } from 'chart.js';
import { LoadingState, ErrorState } from '@/components/PageState';
import { PageHeading } from '@/components/PageHeading';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { Hint } from '@/components/ui/tooltip';
import * as api from '@/api';
import { queryKeys } from '@/lib/queryClient';
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { ANNOTATION_COLOR, DEPLOY_COLOR, annotationBandPlugin, deployMarkerPlugin } from '@/lib/deployMarkerPlugin';
import { annotationBands, tileAnnotationSummary, type AnnotationBand } from './annotationModel';
import { useTickingValue } from '@/hooks/useTicking';
import { MetricKind, type MetricInstrument } from '@/types/metrics';
import type { ApplicationDeployModel } from '@/types/applications';
import {
  MetricDeviationDirection,
  MetricTileKind,
  MetricTileStatusRule,
  type MetricBoardSummary,
  type MetricBoardTile,
  type MetricBoardTileResult,
} from '@/types/metricBoard';
import { SloComparison, SloGaugeAggregate } from '@/types/slo';
import { SloStatePill, inputClass } from '@/pages/slo/shared';
import { errorMessage, errorStatus } from '@/lib/apiError';
import { fractionToPercent, percentToFraction } from '@/pages/slo/sloForm';
import { MetricsTabs } from './MetricsTabs';
import {
  RANGES,
  customRangeLabel,
  deployMarkers,
  latestDeploy,
  parseRangeParams,
  resolveWindow,
  seriesRefetchMs,
  sinceDeployHint,
  sinceDeployRange,
  writeRangeParams,
  type DeployMarker,
  type RangeSelection,
} from './metricModel';
import {
  MAX_TILES,
  acceptsTileInstrument,
  buildTileRequest,
  changeStatusRule,
  changeTileKind,
  defaultAggregate,
  describeRule,
  describeTile,
  emptyTileDraft,
  formatTileChange,
  formatTileValue,
  sparklinePoints,
  acknowledgedHint,
  annotationsTruncatedHint,
  completeUntilHint,
  statusCounts,
  tileStatusClass,
  validateTileDraft,
  type SparkPoint,
} from './boardModel';
import {
  BOARD_PARAM,
  MAX_BOARDS,
  boardReadId,
  boardUrlId,
  deleteBoardDescription,
  readLastBoard,
  selectBoard,
  tileBoardValue,
  validateBoardName,
  writeBoardParam,
  writeLastBoard,
} from './boardsModel';

Chart.register(LineController, LineElement, PointElement, LinearScale, Tooltip);

const toggleClass = (on: boolean) =>
  `h-8 rounded-md border px-3 text-sm ${on ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:bg-muted'}`;

export default function MetricBoardPage() {
  const queryClient = useQueryClient();
  // The window lives in the URL exactly as on the metric page (#327), so a shared link reproduces the board.
  const [searchParams, setSearchParams] = useSearchParams();
  const selection = useMemo(() => parseRangeParams(searchParams), [searchParams]);
  const setSelection = (next: RangeSelection) => setSearchParams((prev) => writeRangeParams(prev, next), { replace: true });
  const { range, custom } = selection;
  const refetchMs = useTickingValue(useCallback((now: number) => seriesRefetchMs(resolveWindow({ range, custom }, now)), [range, custom]));
  const clamped = useTickingValue(useCallback((now: number) => resolveWindow({ range, custom }, now).clamped, [range, custom]));
  const [draft, setDraft] = useState<MetricBoardTile | null>(null);

  // Named boards (§8.34): the selected board is in the URL (?board=<url id of its name>); without one, the last board
  // opened here (localStorage, a convenience) or the Default board.
  const boardsQuery = useQuery({ queryKey: ['metrics', 'boards'] as const, queryFn: api.getMetricBoards });
  const boards = useMemo(() => boardsQuery.data ?? [], [boardsQuery.data]);
  const [lastOpened] = useState(() => readLastBoard());
  const { board: selected, missing } = useMemo(
    () => selectBoard(boards, searchParams.get(BOARD_PARAM), lastOpened),
    [boards, searchParams, lastOpened],
  );
  const readId = boardReadId(selected);
  const showBoard = (next: MetricBoardSummary, list: readonly MetricBoardSummary[] = boards) =>
    setSearchParams((prev) => writeBoardParam(prev, list, next));
  useEffect(() => {
    if (selected && !missing) {
      writeLastBoard(selected.isDefault ? null : boardUrlId(boards, selected));
    }
  }, [boards, selected, missing]);

  const boardQuery = useQuery({
    queryKey: ['metrics', 'board', readId ?? null, range, custom] as const,
    queryFn: () => {
      const w = resolveWindow({ range, custom }, Date.now());

      return api.getMetricBoard(new Date(w.from).toISOString(), new Date(w.to).toISOString(), readId);
    },
    // Wait for the board list (the URL holds a name, the read takes an id); if it fails, the Default board still reads.
    enabled: boardsQuery.isSuccess || boardsQuery.isError,
    refetchInterval: refetchMs,
    placeholderData: keepPreviousData,
  });
  const board = boardQuery.data;

  // Deploys across every application inside the board's window (each tile keeps those of its own application).
  const deploysQuery = useQuery({
    queryKey: ['metrics', 'board', 'deploys', board?.from, board?.to] as const,
    queryFn: () => api.getDeploys({ from: board!.from, to: new Date(Date.parse(board!.to) + 1).toISOString() }),
    enabled: board !== undefined,
    placeholderData: keepPreviousData,
  });
  const latestDeployQuery = useQuery({
    queryKey: ['metrics', 'latest-deploy', null] as const,
    queryFn: () => api.getDeploys({}),
    staleTime: 60_000,
  });
  const lastDeploy = latestDeploy(latestDeployQuery.data);

  const invalidate = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: ['metrics', 'board'] }),
    queryClient.invalidateQueries({ queryKey: ['metrics', 'boards'] }),
  ]);
  const saveMutation = useMutation({
    mutationFn: (d: MetricBoardTile) => api.upsertMetricBoardTile(buildTileRequest(d)),
    onSuccess: () => {
      setDraft(null);
      void invalidate();
    },
    // A 404 on an edit: the tile was deleted meanwhile. Its { error } stays in the form; the board stops showing it.
    onError: (error) => {
      if (errorStatus(error) === 404) {
        void invalidate();
      }
    },
  });
  // Re-read on failure too: a 404 means the tile is already gone, and the board should stop showing it.
  const deleteMutation = useMutation({ mutationFn: (id: number) => api.deleteMetricBoardTile(id), onSettled: () => void invalidate() });
  const [pendingDelete, setPendingDelete] = useState<MetricBoardTile | null>(null);
  const openDraft = (d: MetricBoardTile | null) => {
    saveMutation.reset();
    setDraft(d);
  };

  const [boardDraft, setBoardDraft] = useState<BoardDraft | null>(null);
  const saveBoardMutation = useMutation({
    mutationFn: (d: BoardDraft) => api.upsertMetricBoard({ id: d.id, name: d.name.trim(), description: d.description.trim() || null }),
    onSuccess: async ({ id }) => {
      setBoardDraft(null);
      // The URL id follows the (new) name, so select the saved board from the refreshed list.
      const refreshed = await boardsQuery.refetch();
      const saved = refreshed.data?.find((x) => x.id === id);
      if (saved && refreshed.data) {
        showBoard(saved, refreshed.data);
      }

      void invalidate();
    },
    onError: (error) => {
      if (errorStatus(error) === 404) {
        void invalidate();
      }
    },
  });
  const openBoardDraft = (d: BoardDraft | null) => {
    saveBoardMutation.reset();
    setBoardDraft(d);
  };
  const [pendingBoardDelete, setPendingBoardDelete] = useState<MetricBoardSummary | null>(null);
  const deleteBoardMutation = useMutation({
    mutationFn: (b: MetricBoardSummary) => api.deleteMetricBoard(b.id),
    onSuccess: () => {
      // Back to the Default board, and forget the deleted one.
      writeLastBoard(null);
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.delete(BOARD_PARAM);

        return next;
      });
    },
    onSettled: () => void invalidate(),
  });

  const tiles = board?.tiles ?? [];
  const counts = statusCounts(tiles);
  const span = board ? { from: Date.parse(board.from), to: Date.parse(board.to) } : null;
  const completeHint = board ? completeUntilHint(board) : null;
  const truncatedHint = board ? annotationsTruncatedHint(board) : null;

  return (
    <div>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <PageHeading className="mb-1">Metrics</PageHeading>
          <p className="text-sm text-muted-foreground mb-4">
            The numbers that matter at a glance — each against the same window yesterday and last week, coloured by its
            threshold or its deviation from that baseline.
          </p>
        </div>
        <button
          type="button"
          className={toggleClass(false)}
          disabled={tiles.length >= MAX_TILES}
          onClick={() => openDraft({ ...emptyTileDraft, boardId: selected && !selected.isDefault ? selected.id : null })}
        >
          Add tile
        </button>
      </div>
      <MetricsTabs />

      <BoardPicker
        boards={boards}
        selected={selected}
        loadError={boardsQuery.isError}
        onSelect={(b) => showBoard(b)}
        onNew={() => openBoardDraft({ id: 0, name: '', description: '' })}
        onRename={() => selected && openBoardDraft({ id: selected.id, name: selected.name, description: selected.description ?? '' })}
        onDelete={() => selected && setPendingBoardDelete(selected)}
      />
      {missing !== null && selected && (
        <p role="status" className="mb-3 text-sm text-amber-600">
          No board matches “{missing}” (deleted or renamed); showing {selected.name}.
        </p>
      )}
      {deleteBoardMutation.isError && (
        <p role="alert" className="mb-3 text-sm text-destructive">{errorMessage(deleteBoardMutation.error, 'Failed to delete the board.')}</p>
      )}

      <div className="mb-4 flex flex-wrap items-center gap-2">
        {RANGES.map((r) => (
          <button
            key={r.id}
            type="button"
            className={toggleClass(custom === null && range === r.id)}
            onClick={() => setSelection({ range: r.id, custom: null })}
          >
            {r.label}
          </button>
        ))}
        <Hint text={sinceDeployHint(lastDeploy, latestDeployQuery.isError, false)}>
          <button
            type="button"
            className={toggleClass(false)}
            disabled={lastDeploy === null}
            onClick={() => lastDeploy && setSelection({ range, custom: sinceDeployRange(lastDeploy) })}
          >
            Since last deploy
          </button>
        </Hint>
        {custom !== null && (
          <span className="text-xs text-muted-foreground">
            {customRangeLabel(custom)}
            {clamped && ' (cut to the last 90 days)'}
          </span>
        )}
        <span className="ml-auto flex gap-3 text-xs text-muted-foreground">
          {boardQuery.isPlaceholderData && <span aria-live="polite">updating…</span>}
          {completeHint && (
            <Hint text="Values and their 1d / 7d baselines stop where the processes are still writing data, so complete data is compared with complete data. The sparklines run to the end of the range.">
              <span>{completeHint}</span>
            </Hint>
          )}
          {truncatedHint && (
            <Hint text="More notes overlap this range than the board reads: it keeps the ones active at the end and acknowledged noise first, newest first.">
              <span>{truncatedHint}</span>
            </Hint>
          )}
          {deploysQuery.isError && <span className="text-destructive">Unable to load deploys; no deploy markers are drawn.</span>}
          {counts.breaching > 0 && <span className="text-red-600">{counts.breaching} breaching</span>}
          {counts.warning > 0 && <span className="text-amber-600">{counts.warning} warning</span>}
          {counts.acknowledged > 0 && <span>{counts.acknowledged} acknowledged</span>}
          {counts.noData > 0 && <span>{counts.noData} no data</span>}
        </span>
      </div>

      {deleteMutation.isError && (
        <p role="alert" className="mb-3 text-sm text-destructive">{errorMessage(deleteMutation.error, 'Failed to delete the tile.')}</p>
      )}

      {boardQuery.isError ? (
        <ErrorState message="Unable to load the board" />
      ) : boardQuery.isLoading || !board ? (
        <LoadingState />
      ) : tiles.length === 0 ? (
        <div className="rounded-lg border border-dashed border-border p-10 text-center text-sm text-muted-foreground">
          No tiles on this board yet. Add one, or seed them in code with{' '}
          <code>{selected && !selected.isDefault ? `opt.AddMetricBoard("${selected.name}", b => b.Value(...))` : 'opt.AddMetricBoard(b => b.Value(...).Ratio(...))'}</code>.
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {tiles.map((result) => (
            <TileCard
              key={result.tile.id}
              result={result}
              from={span!.from}
              to={span!.to}
              deploys={deploysQuery.data}
              onEdit={() => openDraft({ ...result.tile })}
              onDelete={() => setPendingDelete(result.tile)}
            />
          ))}
        </div>
      )}

      <ConfirmDialog
        open={pendingDelete !== null}
        onOpenChange={(open) => !open && setPendingDelete(null)}
        title={`Delete tile "${pendingDelete?.name ?? ''}"?`}
        description="Notes on its instrument are kept. A tile seeded in code is re-created on the next start."
        confirmLabel="Delete"
        variant="destructive"
        onConfirm={() => {
          if (pendingDelete) {
            deleteMutation.mutate(pendingDelete.id);
          }
          setPendingDelete(null);
        }}
      />

      <ConfirmDialog
        open={pendingBoardDelete !== null}
        onOpenChange={(open) => !open && setPendingBoardDelete(null)}
        title={`Delete board "${pendingBoardDelete?.name ?? ''}"?`}
        description={pendingBoardDelete ? deleteBoardDescription(pendingBoardDelete) : ''}
        confirmLabel="Delete board"
        variant="destructive"
        onConfirm={() => {
          if (pendingBoardDelete) {
            deleteBoardMutation.mutate(pendingBoardDelete);
          }
          setPendingBoardDelete(null);
        }}
      />

      {boardDraft && (
        <BoardForm
          draft={boardDraft}
          boards={boards}
          onChange={setBoardDraft}
          onCancel={() => openBoardDraft(null)}
          onSave={() => saveBoardMutation.mutate(boardDraft)}
          saving={saveBoardMutation.isPending}
          serverError={saveBoardMutation.isError ? errorMessage(saveBoardMutation.error, 'Failed to save the board.') : null}
        />
      )}

      {draft && (
        <TileForm
          draft={draft}
          boards={boards}
          onChange={setDraft}
          onCancel={() => openDraft(null)}
          onSave={() => saveMutation.mutate(draft)}
          saving={saveMutation.isPending}
          serverError={saveMutation.isError ? errorMessage(saveMutation.error, 'Failed to save the tile.') : null}
        />
      )}
    </div>
  );
}

interface BoardDraft {
  id: number;
  name: string;
  description: string;
}

function BoardPicker({
  boards,
  selected,
  loadError,
  onSelect,
  onNew,
  onRename,
  onDelete,
}: {
  boards: MetricBoardSummary[];
  selected: MetricBoardSummary | undefined;
  loadError: boolean;
  onSelect: (b: MetricBoardSummary) => void;
  onNew: () => void;
  onRename: () => void;
  onDelete: () => void;
}) {
  if (loadError) {
    return <p role="alert" className="mb-3 text-sm text-destructive">Unable to load the boards; showing the Default board.</p>;
  }

  return (
    <div className="mb-3 flex flex-wrap items-center gap-2">
      <label className="flex items-center gap-2 text-sm">
        <span className="text-muted-foreground">Board</span>
        <select
          className={`${inputClass} h-8 w-auto min-w-[12rem]`}
          value={selected?.id ?? ''}
          disabled={boards.length === 0}
          onChange={(e) => {
            const next = boards.find((x) => x.id === Number(e.target.value));
            if (next) {
              onSelect(next);
            }
          }}
        >
          {boards.map((b) => (
            <option key={b.id} value={b.id}>
              {b.name} ({b.tileCount})
            </option>
          ))}
        </select>
      </label>
      <button type="button" className={toggleClass(false)} disabled={boards.length >= MAX_BOARDS} onClick={onNew}>New board</button>
      <button type="button" className={toggleClass(false)} disabled={!selected || selected.id <= 0} onClick={onRename}>Rename</button>
      <Hint text={selected?.isDefault ? 'The Default board holds every tile without a board and cannot be deleted.' : 'Deletes the board and its tiles.'}>
        <span>
          <button type="button" className={`${toggleClass(false)} text-red-600`} disabled={!selected || selected.isDefault} onClick={onDelete}>Delete</button>
        </span>
      </Hint>
      {selected?.description && <span className="text-xs text-muted-foreground">{selected.description}</span>}
    </div>
  );
}

function BoardForm({
  draft,
  boards,
  onChange,
  onCancel,
  onSave,
  saving,
  serverError,
}: {
  draft: BoardDraft;
  boards: MetricBoardSummary[];
  onChange: (d: BoardDraft) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
  serverError: string | null;
}) {
  const clientError = validateBoardName(draft.name, boards, draft.id || null);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div className="w-full max-w-md rounded-xl bg-white p-6 shadow-xl dark:bg-gray-900" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-4 text-lg font-semibold">{draft.id ? 'Rename board' : 'New board'}</h2>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <input className={inputClass} value={draft.name} autoFocus onChange={(e) => onChange({ ...draft, name: e.target.value })} />
          </Field>
          <Field label="Description (optional)">
            <input className={inputClass} value={draft.description} onChange={(e) => onChange({ ...draft, description: e.target.value })} />
          </Field>
        </div>
        {clientError && draft.name !== '' && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
        {serverError && <p role="alert" className="mt-2 text-sm text-red-600">{serverError}</p>}
        <div className="mt-6 flex justify-end gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg px-4 py-2 text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button
            type="button"
            onClick={onSave}
            disabled={saving || clientError !== null}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function TileCard({
  result,
  from,
  to,
  deploys,
  onEdit,
  onDelete,
}: {
  result: MetricBoardTileResult;
  from: number;
  to: number;
  deploys: ApplicationDeployModel[] | undefined;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const { tile } = result;
  const rule = describeRule(tile);
  const points = useMemo(() => sparklinePoints(result), [result]);
  // A tile scoped to one application shows that application's deploys; an unscoped tile shows every deploy.
  const markers = useMemo(() => deployMarkers(deploys ?? [], { from, to }, tile.application), [deploys, from, to, tile.application]);
  // Known-issue notes (#333) the server attached to this tile in the board's one batched read.
  const bands = useMemo(() => annotationBands(result.annotations, { from, to }), [result.annotations, from, to]);
  const ackHint = acknowledgedHint(result);

  return (
    <div className={`rounded-lg border-2 bg-card p-3 ${tileStatusClass(result.status)}`}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <Link
            to={`/metrics/${encodeUrlSafeId(tile.instrument)}`}
            className="block truncate text-sm font-medium hover:underline"
            title={describeTile(tile)}
          >
            {tile.name}
          </Link>
          <div className="truncate font-mono text-[11px] text-muted-foreground">{describeTile(tile)}</div>
        </div>
        {result.status !== null && (ackHint ? (
          <Hint text={ackHint}>
            <span><SloStatePill state={result.status} enabled /></span>
          </Hint>
        ) : <SloStatePill state={result.status} enabled />)}
      </div>

      <div className="mt-2 flex items-baseline justify-between gap-2">
        <span className="text-2xl font-semibold tabular-nums">{formatTileValue(result, result.current)}</span>
        {rule && <span className="text-xs text-muted-foreground">{rule}</span>}
      </div>
      <div className="mt-1 flex gap-4 text-xs text-muted-foreground">
        <span>
          1d <span className="tabular-nums text-foreground">{formatTileValue(result, result.dayAgo)}</span>{' '}
          <span className="tabular-nums">({formatTileChange(tile, result.current, result.dayAgo)})</span>
        </span>
        <span>
          7d <span className="tabular-nums text-foreground">{formatTileValue(result, result.weekAgo)}</span>{' '}
          <span className="tabular-nums">({formatTileChange(tile, result.current, result.weekAgo)})</span>
        </span>
      </div>

      <Sparkline points={points} from={from} to={to} markers={markers} bands={bands} />

      <div className="mt-1 flex items-center justify-between text-xs">
        <span className="flex gap-3 text-muted-foreground">
          {result.annotations.length > 0 && (
            <Hint text={<span className="whitespace-pre-line">{tileAnnotationSummary(result.annotations)}</span>}>
              <span>
                <span className="mr-1 inline-block h-2.5 w-3 rounded-sm align-middle" style={{ backgroundColor: `${ANNOTATION_COLOR}55` }} />
                {result.annotations.length === 1 ? '1 note' : `${result.annotations.length} notes`}
              </span>
            </Hint>
          )}
          {markers.length > 0 && (
            <Hint text={markers.map((m) => m.label).join(', ')}>
              <span>
                <span className="mr-1 inline-block w-3 border-t-2 border-dashed align-middle" style={{ borderColor: DEPLOY_COLOR }} />
                {markers.length === 1 ? '1 deploy' : `${markers.length} deploys`}
              </span>
            </Hint>
          )}
        </span>
        <span className="flex gap-3">
          <button type="button" className="text-muted-foreground hover:underline" onClick={onEdit}>Edit</button>
          <button type="button" className="text-red-600 hover:underline" onClick={onDelete}>Delete</button>
        </span>
      </div>
    </div>
  );
}

function Sparkline({
  points,
  from,
  to,
  markers,
  bands,
  height = 48,
}: {
  points: SparkPoint[];
  from: number;
  to: number;
  markers: DeployMarker[];
  bands: AnnotationBand[];
  height?: number;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    if (!canvasRef.current || points.length === 0) {
      return;
    }

    const chart = new Chart(canvasRef.current, {
      type: 'line',
      plugins: [annotationBandPlugin(bands, false), deployMarkerPlugin(markers, false)],
      data: {
        datasets: [
          {
            data: points,
            borderColor: '#6366f1',
            backgroundColor: '#6366f1',
            borderWidth: 1.5,
            pointRadius: points.length === 1 ? 2 : 0,
            tension: 0,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        parsing: false,
        plugins: { legend: { display: false }, tooltip: { enabled: false } },
        scales: {
          x: { type: 'linear', min: from, max: to, display: false },
          y: { display: false, beginAtZero: false },
        },
      },
    });

    return () => chart.destroy();
  }, [points, from, to, markers, bands]);

  if (points.length === 0) {
    return (
      <div className="mt-2 flex items-center justify-center text-xs text-muted-foreground" style={{ height }}>
        No measurements in this window.
      </div>
    );
  }

  return (
    <div className="mt-2" style={{ height }}>
      <canvas ref={canvasRef} />
    </div>
  );
}

function TileForm({
  draft,
  boards,
  onChange,
  onCancel,
  onSave,
  saving,
  serverError,
}: {
  draft: MetricBoardTile;
  boards: MetricBoardSummary[];
  onChange: (d: MetricBoardTile) => void;
  onCancel: () => void;
  onSave: () => void;
  saving: boolean;
  serverError: string | null;
}) {
  const set = (patch: Partial<MetricBoardTile>) => onChange({ ...draft, ...patch });
  const { data: metrics } = useQuery({ queryKey: queryKeys.metrics, queryFn: api.getMetrics });
  const instruments = (metrics ?? [])
    .filter((x) => acceptsTileInstrument(draft.kind, x.kind))
    .sort((a, b) => a.name.localeCompare(b.name));
  const picked: MetricInstrument | undefined = (metrics ?? []).find((x) => x.name === draft.instrument);
  const instrumentKind = picked?.kind ?? null;
  const ratio = draft.kind === MetricTileKind.Ratio;
  const clientError = validateTileDraft(draft, instrumentKind);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div className="max-h-[90vh] w-full max-w-md overflow-y-auto rounded-xl bg-white p-6 shadow-xl dark:bg-gray-900" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-4 text-lg font-semibold">{draft.id ? 'Edit tile' : 'New tile'}</h2>
        <div className="space-y-3 text-sm">
          <Field label="Name">
            <input className={inputClass} value={draft.name} onChange={(e) => set({ name: e.target.value })} />
          </Field>
          {boards.length > 0 && (
            <Field label="Board">
              <select
                className={inputClass}
                value={tileBoardValue(draft.boardId, boards) ?? ''}
                onChange={(e) => set({ boardId: Number(e.target.value) })}
              >
                {boards.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
              </select>
            </Field>
          )}
          <Field label="Shows">
            <select className={inputClass} value={draft.kind} onChange={(e) => onChange(changeTileKind(draft, Number(e.target.value) as MetricTileKind))}>
              <option value={MetricTileKind.Value}>Value (events, or a gauge's level)</option>
              <option value={MetricTileKind.Ratio}>Ratio (good ÷ good + bad)</option>
            </select>
          </Field>
          <Field label="Instrument">
            <select
              className={`${inputClass} font-mono`}
              value={draft.instrument}
              onChange={(e) => {
                const next = (metrics ?? []).find((x) => x.name === e.target.value);
                set({ instrument: e.target.value, gaugeAggregate: ratio ? null : defaultAggregate(next?.kind ?? null) });
              }}
            >
              <option value="">Select an instrument…</option>
              {draft.instrument !== '' && !instruments.some((x) => x.name === draft.instrument) && <option value={draft.instrument}>{draft.instrument}</option>}
              {instruments.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
            </select>
          </Field>
          {!ratio && instrumentKind === MetricKind.Gauge && (
            <Field label="Gauge value over the window">
              <select className={inputClass} value={draft.gaugeAggregate ?? SloGaugeAggregate.Average} onChange={(e) => set({ gaugeAggregate: Number(e.target.value) as SloGaugeAggregate })}>
                <option value={SloGaugeAggregate.Average}>Average (every sample, never summed)</option>
                <option value={SloGaugeAggregate.Min}>Minimum</option>
                <option value={SloGaugeAggregate.Max}>Maximum</option>
              </select>
            </Field>
          )}
          {ratio && (
            <>
              <Field label="Ratio tag">
                <TagKeySelect value={draft.ratioTag ?? ''} keys={picked?.tagKeys ?? []} onChange={(ratioTag) => set({ ratioTag })} />
              </Field>
              <Field label="Good values (comma-separated, trailing * allowed)">
                <input className={`${inputClass} font-mono`} placeholder="ok,authorised" value={draft.goodValues ?? ''} onChange={(e) => set({ goodValues: e.target.value })} />
              </Field>
              <Field label="Bad values (comma-separated, trailing * allowed)">
                <input className={`${inputClass} font-mono`} placeholder="declined,error" value={draft.badValues ?? ''} onChange={(e) => set({ badValues: e.target.value })} />
              </Field>
            </>
          )}
          <Field label="Tag filter (k=v,k=v; trailing * allowed)">
            <input className={`${inputClass} font-mono`} placeholder="http.response.status_code=5*" value={draft.tagFilter ?? ''} onChange={(e) => set({ tagFilter: e.target.value })} />
          </Field>
          <Field label="Application (optional)">
            <input className={inputClass} value={draft.application ?? ''} onChange={(e) => set({ application: e.target.value || null })} />
          </Field>

          <Field label="Status colour">
            <select className={inputClass} value={draft.statusRule} onChange={(e) => onChange(changeStatusRule(draft, Number(e.target.value) as MetricTileStatusRule))}>
              <option value={MetricTileStatusRule.None}>None</option>
              <option value={MetricTileStatusRule.Threshold}>Threshold</option>
              <option value={MetricTileStatusRule.Deviation}>Deviation from a baseline</option>
            </select>
          </Field>
          {draft.statusRule === MetricTileStatusRule.Threshold && (
            <div className="flex gap-2">
              <select className={inputClass} value={draft.comparison ?? SloComparison.AtMost} onChange={(e) => set({ comparison: Number(e.target.value) as SloComparison })}>
                <option value={SloComparison.AtMost}>At most</option>
                <option value={SloComparison.AtLeast}>At least</option>
              </select>
              <input
                className={`${inputClass} tabular-nums`}
                type="number"
                step="any"
                aria-label={ratio ? 'Threshold (%)' : 'Threshold'}
                value={draft.threshold === null ? '' : ratio ? fractionToPercent(draft.threshold) : draft.threshold}
                onChange={(e) => set({ threshold: e.target.value === '' ? null : ratio ? percentToFraction(Number(e.target.value)) : Number(e.target.value) })}
              />
              {ratio && <span className="self-center text-muted-foreground">%</span>}
            </div>
          )}
          {draft.statusRule === MetricTileStatusRule.Deviation && (
            <>
              <Field label="Bad move">
                <select className={inputClass} value={draft.deviationDirection ?? MetricDeviationDirection.Either} onChange={(e) => set({ deviationDirection: Number(e.target.value) as MetricDeviationDirection })}>
                  <option value={MetricDeviationDirection.Drop}>A drop</option>
                  <option value={MetricDeviationDirection.Rise}>A rise</option>
                  <option value={MetricDeviationDirection.Either}>Either way</option>
                </select>
              </Field>
              <Field label={ratio ? 'Allowed move (percentage points)' : 'Allowed move (% of the baseline)'}>
                <input
                  className={`${inputClass} tabular-nums`}
                  type="number"
                  step="any"
                  value={draft.maxDeviation === null ? '' : fractionToPercent(draft.maxDeviation)}
                  onChange={(e) => set({ maxDeviation: e.target.value === '' ? null : percentToFraction(Number(e.target.value)) })}
                />
              </Field>
              <Field label="Baseline">
                <select className={inputClass} value={draft.baselineDays ?? 7} onChange={(e) => set({ baselineDays: Number(e.target.value) })}>
                  <option value={1}>Same window 1 day earlier</option>
                  <option value={7}>Same window 7 days earlier</option>
                </select>
              </Field>
            </>
          )}
        </div>
        {clientError && <p className="mt-4 text-xs text-amber-600">{clientError}</p>}
        {serverError && <p role="alert" className="mt-2 text-sm text-red-600">{serverError}</p>}
        <div className="mt-6 flex justify-end gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg px-4 py-2 text-sm text-gray-600 hover:bg-gray-100 dark:hover:bg-gray-800">Cancel</button>
          <button
            type="button"
            onClick={onSave}
            disabled={saving || clientError !== null}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}

function TagKeySelect({ value, keys, onChange }: { value: string; keys: string[]; onChange: (v: string) => void }) {
  if (keys.length === 0) {
    return <input className={`${inputClass} font-mono`} value={value} onChange={(e) => onChange(e.target.value)} />;
  }

  return (
    <select className={`${inputClass} font-mono`} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Select a tag…</option>
      {value !== '' && !keys.includes(value) && <option value={value}>{value}</option>}
      {keys.map((k) => <option key={k} value={k}>{k}</option>)}
    </select>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs text-gray-500">{label}</span>
      {children}
    </label>
  );
}
