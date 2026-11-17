// Pure model of the named-board picker on Metrics → Board (§8.34): the URL id of a board, which board a URL or the
// remembered last board selects, and the name rules the server enforces (so the form can say so before a round trip).
import type { MetricBoardSummary } from '@/types/metricBoard';

/** The URL parameter that holds the selected board. */
export const BOARD_PARAM = 'board';

/** The localStorage key of the last board opened (a convenience; the URL is the source of truth). */
export const LAST_BOARD_KEY = 'warp:metrics:lastBoard';

/** MetricBoardValidation.MaxBoardNameLength. */
export const MAX_BOARD_NAME = 200;

/** MetricBoardValidation.MaxBoards. */
export const MAX_BOARDS = 100;

/** A board name as a URL-safe slug: lower case, accents dropped, every run of other characters one dash. */
export function boardSlug(name: string): string {
  const slug = name
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');

  return slug === '' ? 'board' : slug;
}

/**
 * The URL id of every board, unique: a board's slug, or `slug-{id}` when an earlier board (in picker order) already has
 * that slug — two names the server keeps apart ("Check-out" and "Check out") can share a slug.
 */
export function boardUrlIds(boards: readonly MetricBoardSummary[]): Map<number, string> {
  const ids = new Map<number, string>();
  const taken = new Set<string>();
  for (const board of boards) {
    const slug = boardSlug(board.name);
    const id = taken.has(slug) ? `${slug}-${board.id}` : slug;
    taken.add(id);
    ids.set(board.id, id);
  }

  return ids;
}

/** The URL id of one board among `boards`. */
export function boardUrlId(boards: readonly MetricBoardSummary[], board: MetricBoardSummary): string {
  return boardUrlIds(boards).get(board.id) ?? boardSlug(board.name);
}

/** The board a URL id names, or undefined. */
export function findBoard(boards: readonly MetricBoardSummary[], urlId: string | null | undefined): MetricBoardSummary | undefined {
  if (!urlId) {
    return undefined;
  }

  const ids = boardUrlIds(boards);

  return boards.find((x) => ids.get(x.id) === urlId);
}

export interface BoardSelection {
  /** The board to show; undefined until the boards are known. */
  board: MetricBoardSummary | undefined;
  /** The URL named a board that does not exist (deleted, renamed): the page says so and shows the fallback. */
  missing: string | null;
}

/**
 * Which board the page shows: the URL's, else the last board opened (when it still exists), else the Default board.
 * A URL naming no board falls back the same way and is reported as missing; a stale remembered board is not.
 */
export function selectBoard(boards: readonly MetricBoardSummary[], urlId: string | null, lastOpened: string | null): BoardSelection {
  const fallback = findBoard(boards, lastOpened) ?? boards.find((x) => x.isDefault) ?? boards[0];
  if (!urlId) {
    return { board: fallback, missing: null };
  }

  const named = findBoard(boards, urlId);

  return named ? { board: named, missing: null } : { board: fallback, missing: urlId };
}

/** The board id the board read sends: none for the Default board (the server's default, and a never-created one has id 0). */
export function boardReadId(board: MetricBoardSummary | undefined): number | undefined {
  return board === undefined || board.isDefault ? undefined : board.id;
}

/** The search params with the board selected: the Default board leaves the parameter out, so plain links stay short. */
export function writeBoardParam(params: URLSearchParams, boards: readonly MetricBoardSummary[], board: MetricBoardSummary): URLSearchParams {
  const next = new URLSearchParams(params);
  if (board.isDefault) {
    next.delete(BOARD_PARAM);
  } else {
    next.set(BOARD_PARAM, boardUrlId(boards, board));
  }

  return next;
}

/** The client-side twin of the server's board-name rules; null when the name is acceptable. */
export function validateBoardName(name: string, boards: readonly MetricBoardSummary[], editingId: number | null): string | null {
  const trimmed = name.trim();
  if (trimmed === '') {
    return 'A board needs a name.';
  }

  if (trimmed.length > MAX_BOARD_NAME) {
    return `A board name is at most ${MAX_BOARD_NAME} characters.`;
  }

  const lower = trimmed.toLowerCase();
  if (boards.some((x) => x.id !== editingId && x.name.trim().toLowerCase() === lower)) {
    return `A board named "${trimmed}" already exists.`;
  }

  return null;
}

/** The tile editor's board choice: a tile with no board is on the Default board. */
export function tileBoardValue(boardId: number | null, boards: readonly MetricBoardSummary[]): number | null {
  if (boardId !== null) {
    return boardId;
  }

  return boards.find((x) => x.isDefault)?.id ?? null;
}

/** The confirm text for deleting a board, which deletes its tiles with it. */
export function deleteBoardDescription(board: MetricBoardSummary): string {
  const tiles = board.tileCount === 1 ? '1 tile' : `${board.tileCount} tiles`;

  return `Its ${tiles} are deleted with it. A board seeded in code is re-created, with its seeded tiles, on the next start.`;
}

/** Reads the last board opened; null when storage is unavailable (private mode, blocked site data) or empty. */
export function readLastBoard(storage: Pick<Storage, 'getItem'> | undefined = safeLocalStorage()): string | null {
  try {
    return storage?.getItem(LAST_BOARD_KEY) ?? null;
  } catch {
    return null;
  }
}

/** Remembers the last board opened (null forgets it); storage failures are ignored, the URL still holds the board. */
export function writeLastBoard(urlId: string | null, storage: Pick<Storage, 'setItem' | 'removeItem'> | undefined = safeLocalStorage()): void {
  try {
    if (urlId === null) {
      storage?.removeItem(LAST_BOARD_KEY);
    } else {
      storage?.setItem(LAST_BOARD_KEY, urlId);
    }
  } catch {
    // Storage is a convenience only.
  }
}

function safeLocalStorage(): Storage | undefined {
  try {
    return typeof localStorage === 'undefined' ? undefined : localStorage;
  } catch {
    return undefined;
  }
}
