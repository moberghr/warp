// The tag-filter pattern grammar, shared by the Metrics pages (filter chips, breakdown numerators) and the SLO editor
// (tag filters, good/bad value lists): the client mirror of Warp.Core.Slo.MetricFilter / MetricValuePattern. One
// module, so the page, the editor and the server's evaluator can never disagree on what a pattern matches.

/** The value an absent tag reads as. Only an exact `{none}` pattern matches it. */
export const NONE_NAME = '{none}';

export interface FilterChip {
  key: string;
  /** May end in `*` for a prefix match. */
  value: string;
}

/** One parsed value pattern: `text` alone, or a prefix match when the pattern ended in `*`. */
export interface ValuePattern {
  text: string;
  prefix: boolean;
}

/** Parses one value pattern. Null when `*` appears anywhere but as the last character. */
export function parsePattern(value: string): ValuePattern | null {
  const star = value.indexOf('*');
  if (star >= 0 && star !== value.length - 1) {
    return null;
  }

  return star >= 0 ? { text: value.slice(0, -1), prefix: true } : { text: value, prefix: false };
}

/** Parses "key=value". The key may not hold `:` (the API splits on the first one). Null when malformed. */
export function parseFilterChip(text: string): FilterChip | null {
  const separator = text.indexOf('=');
  if (separator <= 0) {
    return null;
  }

  const key = text.slice(0, separator).trim();
  const value = text.slice(separator + 1).trim();
  if (!key || !value || key.includes(':')) {
    return null;
  }

  return { key, value };
}

/**
 * One tag value against one filter pattern, with the server's (and the SLO evaluator's) rules: a trailing `*` is a
 * prefix match, so a bare `*` matches any PRESENT value; `actual === null` is an absent tag, which reads as
 * `{none}` and is matched only by an exact `{none}` (never by a prefix, not even `*`). Ordinal comparison.
 */
export function matchesPattern(actual: string | null, pattern: string): boolean {
  const isPrefix = pattern.endsWith('*');
  if (actual === null) {
    return !isPrefix && pattern === NONE_NAME;
  }

  return isPrefix ? actual.startsWith(pattern.slice(0, -1)) : actual === pattern;
}

export function matchesAny(actual: string | null, patterns: string[]): boolean {
  return patterns.some((x) => matchesPattern(actual, x));
}

/** True when some value could match both patterns: equal literals, a literal under a prefix, or nested prefixes. */
export function patternsOverlap(a: ValuePattern, b: ValuePattern): boolean {
  if (!a.prefix && !b.prefix) {
    return a.text === b.text;
  }
  if (a.prefix && b.prefix) {
    return a.text.startsWith(b.text) || b.text.startsWith(a.text);
  }
  const literal = a.prefix ? b.text : a.text;
  const prefix = a.prefix ? a.text : b.text;

  return literal.startsWith(prefix);
}

/** True when some pattern of one list overlaps some pattern of the other. */
export function listsOverlap(first: ValuePattern[], second: ValuePattern[]): boolean {
  return first.some((x) => second.some((y) => patternsOverlap(x, y)));
}

/**
 * True when every value `narrower` matches is matched by `pattern` too (MetricValuePattern.Includes): an equal literal,
 * or a literal or longer prefix under a prefix. A prefix (even a bare `*`) never includes the absent tag `{none}`.
 */
export function patternIncludes(pattern: ValuePattern, narrower: ValuePattern): boolean {
  if (!pattern.prefix) {
    return !narrower.prefix && pattern.text === narrower.text;
  }
  if (!narrower.prefix && narrower.text === NONE_NAME) {
    return false;
  }

  return narrower.text.startsWith(pattern.text);
}

/** A parsed `k=v[,k=v...]` filter: one pattern per key. Empty means "everything". */
export type TagFilter = Map<string, ValuePattern>;

/**
 * Parses a tag filter with the server's grammar (MetricFilter.TryParse): blank is the empty filter; null when an entry
 * is empty, lacks `=`, has an empty or starred key, a misplaced `*`, or a duplicate key.
 */
export function parseTagFilter(text: string | null | undefined): TagFilter | null {
  const filter: TagFilter = new Map();
  if (!text || text.trim() === '') {
    return filter;
  }

  for (const raw of text.split(',')) {
    const pair = raw.trim();
    const separator = pair.indexOf('=');
    if (separator < 0) {
      return null;
    }

    const key = pair.slice(0, separator).trim();
    const pattern = parsePattern(pair.slice(separator + 1).trim());
    if (key === '' || key.includes('*') || !pattern || filter.has(key)) {
      return null;
    }

    filter.set(key, pattern);
  }

  return filter;
}

function isAbsent(pattern: ValuePattern): boolean {
  return !pattern.prefix && pattern.text === NONE_NAME;
}

/**
 * True when some tag set could match both filters (MetricFilter.Overlaps): every key both constrain has overlapping
 * patterns; `{none}` overlaps only itself. The rule that puts a note on a board tile.
 */
export function filtersOverlap(a: TagFilter, b: TagFilter): boolean {
  for (const [key, pattern] of a) {
    const theirs = b.get(key);
    if (!theirs) {
      continue;
    }

    const absent = isAbsent(pattern) || isAbsent(theirs);
    if (absent ? pattern.prefix !== theirs.prefix || pattern.text !== theirs.text : !patternsOverlap(pattern, theirs)) {
      return false;
    }
  }

  return true;
}

/**
 * True when every tag set `narrower` matches is matched by `filter` too (MetricFilter.Covers): each clause of `filter`
 * is implied by `narrower`'s clause on the same key. A key only `filter` constrains is not covered. The rule that lets
 * an acknowledged-noise note change a tile's verdict: covering it, not merely overlapping it.
 */
export function filterCovers(filter: TagFilter, narrower: TagFilter): boolean {
  for (const [key, pattern] of filter) {
    const theirs = narrower.get(key);
    if (!theirs || !patternIncludes(pattern, theirs)) {
      return false;
    }
  }

  return true;
}
