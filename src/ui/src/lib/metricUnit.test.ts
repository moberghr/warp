import { describe, expect, it } from 'vitest';
import { pluralize, unitLabel, withUnit } from './metricUnit';

describe('unitLabel', () => {
  it.each<[string | null | undefined, number | null | undefined, string]>([
    // No unit text: missing, empty, and UCUM's dimensionless `1`.
    [null, undefined, ''],
    [undefined, 5, ''],
    ['', 5, ''],
    ['1', 5, ''],
    ['1', 1, ''],
    // Real units, unchanged (bytes keep their spelled-out form).
    ['s', 5, 's'],
    ['ms', 1, 'ms'],
    ['By', 5, 'bytes'],
    ['By', 1, 'bytes'],
    ['%', 5, '%'],
    ['By/s', 5, 'By/s'],
    ['1/s', 5, '1/s'],
    // Annotations: the word, plural unless the count is exactly 1, plural with no count.
    ['{callback}', 30159, 'callbacks'],
    ['{checkout}', 1639, 'checkouts'],
    ['{request}', 1, 'request'],
    ['{request}', 0, 'requests'],
    ['{request}', 1.5, 'requests'],
    ['{request}', -1, 'requests'],
    ['{request}', undefined, 'requests'],
    ['{request}', null, 'requests'],
    ['{status}', 3, 'status'],
    ['{tcp connection}', 2, 'tcp connections'],
    ['{ checkout }', 2, 'checkouts'],
    ['{}', 2, ''],
    // Compound forms: each part read, only the counted (first) part pluralised.
    ['{request}/s', 12, 'requests/s'],
    ['{request}/s', 1, 'request/s'],
    ['{request}/s', undefined, 'requests/s'],
    ['By/{request}', 12, 'By/request'],
    // An annotation attached to a real unit is dropped, as UCUM ignores it.
    ['ms{server}', 3, 'ms'],
  ])('%s with count %s reads "%s"', (unit, count, expected) => {
    expect(unitLabel(unit, count)).toBe(expected);
  });
});

describe('withUnit', () => {
  it('appends the unit read for the count, or leaves the text alone', () => {
    expect(withUnit('30,159', '{callback}', 30159)).toBe('30,159 callbacks');
    expect(withUnit('1', '{checkout}', 1)).toBe('1 checkout');
    expect(withUnit('250', 'ms', 250)).toBe('250 ms');
    expect(withUnit('12', '1', 12)).toBe('12');
    expect(withUnit('12', null, 12)).toBe('12');
  });
});

describe('pluralize', () => {
  it.each([
    ['callback', 'callbacks'],
    ['request', 'requests'],
    ['bytes', 'bytes'],
    ['', ''],
  ])('%s → %s', (word, expected) => {
    expect(pluralize(word)).toBe(expected);
  });
});
