import { describe, expect, it } from 'vitest';
import {
  filterCovers,
  filtersOverlap,
  listsOverlap,
  matchesAny,
  matchesPattern,
  parseFilterChip,
  parsePattern,
  parseTagFilter,
  patternIncludes,
  patternsOverlap,
  type TagFilter,
} from './metricPattern';

describe('parseFilterChip', () => {
  it('parses key=value and rejects malformed input', () => {
    expect(parseFilterChip('result=ok')).toEqual({ key: 'result', value: 'ok' });
    expect(parseFilterChip('  http.response.status_code = 5* ')).toEqual({ key: 'http.response.status_code', value: '5*' });
    expect(parseFilterChip('novalue')).toBeNull();
    expect(parseFilterChip('=x')).toBeNull();
    expect(parseFilterChip('k=')).toBeNull();
    // the API splits on the first ':' so a key holding one could never round-trip
    expect(parseFilterChip('a:b=c')).toBeNull();
  });

  it('keeps a value containing = intact', () => {
    expect(parseFilterChip('route=/a=b')).toEqual({ key: 'route', value: '/a=b' });
  });
});

describe('matchesPattern', () => {
  // Mirrors MatchingTable in src/tests/Warp.Tests/Slo/MetricFilterTests.cs row for row: the page and the SLO
  // evaluator share one set of rules. `null` is an absent tag (a stored tag set without the key).
  const table: [pattern: string, actual: string | null, expected: boolean][] = [
    ['500', null, false],
    ['500', '500', true],
    ['500', '5000', false],
    ['5*', '503', true],
    ['5*', '5', true],
    ['5*', '403', false],
    ['5*', null, false],
    ['*', '200', true],
    ['*', '', true],
    ['*', null, false],
    ['{none}', null, true],
    ['{none}', '200', false],
    ['{no*', null, false],
    ['{none}', '{none}', true],
  ];

  it.each(table)('pattern %j against %j is %j', (pattern, actual, expected) => {
    expect(matchesPattern(actual, pattern)).toBe(expected);
  });

  it('matches any of several patterns', () => {
    expect(matchesAny('503', ['2*', '5*'])).toBe(true);
    expect(matchesAny(null, ['*', '2*'])).toBe(false);
  });
});

describe('parsePattern', () => {
  it('reads a trailing * as a prefix and refuses a * anywhere else', () => {
    expect(parsePattern('200')).toEqual({ text: '200', prefix: false });
    expect(parsePattern('5*')).toEqual({ text: '5', prefix: true });
    expect(parsePattern('*')).toEqual({ text: '', prefix: true });
    expect(parsePattern('a*b')).toBeNull();
    expect(parsePattern('**')).toBeNull();
  });
});

describe('patternsOverlap', () => {
  const p = (value: string) => parsePattern(value)!;

  it.each([
    ['200', '200', true],
    ['200', '201', false],
    ['5*', '503', true],
    ['5*', '403', false],
    ['5*', '50*', true],
    ['*', 'anything', true],
  ])('%j and %j overlap: %j', (a, b, expected) => {
    expect(patternsOverlap(p(a), p(b))).toBe(expected);
    expect(patternsOverlap(p(b), p(a))).toBe(expected);
  });

  it('compares whole lists', () => {
    expect(listsOverlap([p('2*')], [p('4*'), p('201')])).toBe(true);
    expect(listsOverlap([p('2*')], [p('4*'), p('5*')])).toBe(false);
    expect(listsOverlap([], [p('*')])).toBe(false);
  });
});

// Mirrors of MetricFilter.TryParse / Overlaps / Covers and MetricValuePattern.Includes (Warp.Core.Slo): the demo board
// decides which notes show on a tile (overlap) and which may acknowledge it (cover) with these.
const f = (text: string | null): TagFilter => {
  const parsed = parseTagFilter(text);
  if (!parsed) {
    throw new Error(`unparseable filter ${text}`);
  }

  return parsed;
};
const p = (text: string) => parsePattern(text)!;

describe('parseTagFilter', () => {
  it('reads blank as the empty filter and one pattern per key', () => {
    expect(f(null).size).toBe(0);
    expect(f('  ').size).toBe(0);
    expect([...f(' a = 5* , b=x ')]).toEqual([['a', { text: '5', prefix: true }], ['b', { text: 'x', prefix: false }]]);
  });

  it('rejects what the server rejects', () => {
    for (const bad of ['a=1,', 'a', '=1', 'a*=1', 'a=1*2', 'a=1,a=2']) {
      expect(parseTagFilter(bad)).toBeNull();
    }
  });
});

describe('patternIncludes', () => {
  it('includes an equal literal, and a literal or longer prefix under a prefix', () => {
    expect(patternIncludes(p('503'), p('503'))).toBe(true);
    expect(patternIncludes(p('5*'), p('503'))).toBe(true);
    expect(patternIncludes(p('5*'), p('50*'))).toBe(true);
    expect(patternIncludes(p('5*'), p('5*'))).toBe(true);
    expect(patternIncludes(p('*'), p('anything'))).toBe(true);
  });

  it('never lets a literal include a prefix, a narrower prefix include a wider one, or a prefix include {none}', () => {
    expect(patternIncludes(p('503'), p('5*'))).toBe(false);
    expect(patternIncludes(p('50*'), p('5*'))).toBe(false);
    expect(patternIncludes(p('503'), p('504'))).toBe(false);
    expect(patternIncludes(p('*'), p('{none}'))).toBe(false);
    expect(patternIncludes(p('{none}'), p('{none}'))).toBe(true);
  });
});

describe('filtersOverlap', () => {
  it('overlaps when no shared key contradicts', () => {
    expect(filtersOverlap(f(''), f('a=1'))).toBe(true);
    expect(filtersOverlap(f('a=1'), f('b=2'))).toBe(true);
    expect(filtersOverlap(f('s=503'), f('s=5*'))).toBe(true);
    expect(filtersOverlap(f('s=5*'), f('s=50*'))).toBe(true);
  });

  it('does not overlap on contradicting values, and {none} overlaps only itself', () => {
    expect(filtersOverlap(f('s=4*'), f('s=5*'))).toBe(false);
    expect(filtersOverlap(f('s=500'), f('s=503'))).toBe(false);
    expect(filtersOverlap(f('s={none}'), f('s=*'))).toBe(false);
    expect(filtersOverlap(f('s={none}'), f('s={none}'))).toBe(true);
  });
});

describe('filterCovers', () => {
  it('covers when every clause is implied by the narrower filter', () => {
    expect(filterCovers(f(''), f(''))).toBe(true);
    expect(filterCovers(f(''), f('s=5*'))).toBe(true);
    expect(filterCovers(f('s=5*'), f('s=5*'))).toBe(true);
    expect(filterCovers(f('s=5*'), f('s=503,region=eu'))).toBe(true);
  });

  it('does not cover a part: a note key the tile lets through, or a narrower note pattern', () => {
    expect(filterCovers(f('outcome=error'), f(''))).toBe(false);
    expect(filterCovers(f('s=503'), f('s=5*'))).toBe(false);
    expect(filterCovers(f('s=5*,region=eu'), f('s=503'))).toBe(false);
  });
});
