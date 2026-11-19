// Metric units as people read them. OpenTelemetry units are UCUM codes: `ms`, `s`, `By`, `1` (dimensionless), and the
// curly-brace annotation form `{request}`, which is a dimensionless count annotated "request". Every place the dashboard
// prints a metric unit goes through `unitLabel`, so `{checkout}` never reaches the screen raw.

// A whole unit that is only an annotation, e.g. `{request}`.
const ANNOTATION_ONLY = /^\{([^{}]*)\}$/;

// An annotation attached to a unit, e.g. the `{server}` of `ms{server}`: UCUM ignores it for the unit's meaning.
const ATTACHED_ANNOTATION = /\{[^{}]*\}/g;

/**
 * The unit as display text, or `''` for no unit text.
 *
 * - `null`, `''` and `1` (UCUM dimensionless) print nothing; `By` prints `bytes`.
 * - `{x}` prints the word `x`, pluralised naively (`request` → `requests`) unless `count` is exactly 1; a word already
 *   ending in `s` is left alone. With no count (an axis title, a unit column) it is plural.
 * - A compound unit has each part read the same way and only the first (the counted thing) pluralised:
 *   `{request}/s` → `requests/s`, `By/{request}` → `By/request`.
 * - Every other unit (`s`, `ms`, `%`, `By/s`) is returned unchanged.
 */
export function unitLabel(unit: string | null | undefined, count?: number | null): string {
  if (!unit || unit === '1') {
    return '';
  }

  if (unit === 'By') {
    return 'bytes';
  }

  if (!unit.includes('{')) {
    return unit;
  }

  const plural = count !== 1;

  return unit
    .split('/')
    .map((part, index) => readPart(part, plural && index === 0))
    .join('/');
}

/** `value` followed by its unit (`"12 requests"`, `"1 request"`, `"250 ms"`), or the value alone when there is none. */
export function withUnit(text: string, unit: string | null | undefined, count?: number | null): string {
  const label = unitLabel(unit, count);

  return label ? `${text} ${label}` : text;
}

/** Naive English plural: `callback` → `callbacks`; a word already ending in `s` is left alone. */
export function pluralize(word: string): string {
  return word === '' || word.endsWith('s') ? word : `${word}s`;
}

function readPart(part: string, plural: boolean): string {
  const annotation = ANNOTATION_ONLY.exec(part);
  if (annotation) {
    const word = annotation[1].trim();

    return plural ? pluralize(word) : word;
  }

  return part.replace(ATTACHED_ANNOTATION, '');
}
