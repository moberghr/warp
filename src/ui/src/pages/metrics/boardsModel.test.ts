import { describe, expect, it } from 'vitest';
import type { MetricBoardSummary } from '@/types/metricBoard';
import {
  BOARD_PARAM,
  LAST_BOARD_KEY,
  boardReadId,
  boardSlug,
  boardUrlId,
  boardUrlIds,
  deleteBoardDescription,
  findBoard,
  readLastBoard,
  selectBoard,
  tileBoardValue,
  validateBoardName,
  writeBoardParam,
  writeLastBoard,
} from './boardsModel';

const board = (id: number, name: string, patch: Partial<MetricBoardSummary> = {}): MetricBoardSummary => ({
  id,
  name,
  description: null,
  position: id,
  application: null,
  isDefault: false,
  tileCount: 0,
  createdBy: null,
  ...patch,
});

const boards = [board(1, 'Default', { isDefault: true }), board(2, 'Checkout'), board(3, 'Payments & Refunds')];

describe('boardSlug', () => {
  it.each([
    ['Checkout', 'checkout'],
    ['Payments & Refunds', 'payments-refunds'],
    ['  Café Orders ', 'cafe-orders'],
    ['--', 'board'],
    ['', 'board'],
  ])('%s -> %s', (name, slug) => {
    expect(boardSlug(name)).toBe(slug);
  });
});

describe('boardUrlIds', () => {
  it('is the slug, with the id appended to a later board whose slug is taken', () => {
    const ids = boardUrlIds([board(1, 'Default', { isDefault: true }), board(2, 'Check out'), board(5, 'Check-out')]);

    expect([...ids.values()]).toEqual(['default', 'check-out', 'check-out-5']);
  });

  it('round-trips every board through findBoard', () => {
    for (const b of boards) {
      expect(findBoard(boards, boardUrlId(boards, b))).toBe(b);
    }
  });
});

describe('selectBoard', () => {
  it('takes the URL board first', () => {
    expect(selectBoard(boards, 'checkout', 'payments-refunds')).toEqual({ board: boards[1], missing: null });
  });

  it('falls back to the last board opened, then to the Default board', () => {
    expect(selectBoard(boards, null, 'payments-refunds').board).toBe(boards[2]);
    expect(selectBoard(boards, null, 'gone').board).toBe(boards[0]);
    expect(selectBoard(boards, null, null)).toEqual({ board: boards[0], missing: null });
  });

  it('reports a URL board that no longer exists and shows the fallback', () => {
    expect(selectBoard(boards, 'deleted', null)).toEqual({ board: boards[0], missing: 'deleted' });
  });

  it('selects nothing before the boards are known', () => {
    expect(selectBoard([], null, null).board).toBeUndefined();
  });
});

describe('boardReadId and writeBoardParam', () => {
  it('reads the Default board without an id and a named board by id', () => {
    expect(boardReadId(boards[0])).toBeUndefined();
    expect(boardReadId(board(0, 'Default', { isDefault: true }))).toBeUndefined();
    expect(boardReadId(boards[1])).toBe(2);
    expect(boardReadId(undefined)).toBeUndefined();
  });

  it('keeps the other parameters and leaves the Default board out of the URL', () => {
    const named = writeBoardParam(new URLSearchParams('range=7d'), boards, boards[1]);
    const back = writeBoardParam(named, boards, boards[0]);

    expect(named.toString()).toBe(`range=7d&${BOARD_PARAM}=checkout`);
    expect(back.toString()).toBe('range=7d');
  });
});

describe('validateBoardName', () => {
  it('accepts a new name and the edited board keeping its own name', () => {
    expect(validateBoardName('Shipping', boards, null)).toBeNull();
    expect(validateBoardName(' checkout ', boards, 2)).toBeNull();
  });

  it.each([
    ['  ', null, 'needs a name'],
    ['x'.repeat(201), null, 'at most 200'],
    ['CHECKOUT', null, 'already exists'],
    ['default', 2, 'already exists'],
  ])('rejects %j', (name, editing, message) => {
    expect(validateBoardName(name, boards, editing)).toContain(message);
  });
});

describe('tileBoardValue', () => {
  it('puts a tile without a board on the Default board', () => {
    expect(tileBoardValue(null, boards)).toBe(1);
    expect(tileBoardValue(3, boards)).toBe(3);
    expect(tileBoardValue(null, [])).toBeNull();
  });
});

describe('deleteBoardDescription', () => {
  it('says the tiles go with the board', () => {
    expect(deleteBoardDescription(board(2, 'Checkout', { tileCount: 1 }))).toContain('Its 1 tile are deleted');
    expect(deleteBoardDescription(board(2, 'Checkout', { tileCount: 4 }))).toContain('Its 4 tiles are deleted');
  });
});

describe('last board storage', () => {
  const memory = () => {
    const items = new Map<string, string>();

    return {
      items,
      getItem: (k: string) => items.get(k) ?? null,
      setItem: (k: string, v: string) => void items.set(k, v),
      removeItem: (k: string) => void items.delete(k),
    };
  };
  const throwing = {
    getItem: () => {
      throw new Error('blocked');
    },
    setItem: () => {
      throw new Error('blocked');
    },
    removeItem: () => {
      throw new Error('blocked');
    },
  };

  it('remembers and forgets the last board', () => {
    const storage = memory();

    writeLastBoard('checkout', storage);
    expect(storage.items.get(LAST_BOARD_KEY)).toBe('checkout');
    expect(readLastBoard(storage)).toBe('checkout');
    writeLastBoard(null, storage);
    expect(readLastBoard(storage)).toBeNull();
  });

  it('never throws when storage is unavailable', () => {
    expect(readLastBoard(throwing)).toBeNull();
    expect(() => writeLastBoard('checkout', throwing)).not.toThrow();
    expect(readLastBoard(undefined)).toBeNull();
  });
});
