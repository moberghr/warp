import { describe, expect, it } from 'vitest';
import { listsOverlap, matchesAny, matchesPattern, parseFilterChip, parsePattern, patternsOverlap } from './metricPattern';

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
