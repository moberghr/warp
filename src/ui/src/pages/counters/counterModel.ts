import { shortType } from '@/utils/format';

// The Counters page reads ONE flat key/value list that every Warp subsystem writes into (§6.2: hot paths write
// `Counter` rows, `CounterAggregator` folds them into `Statistic`). That list is deliberately heterogeneous —
// global job outcomes, per-job-type execution stats, queue-wait latency, adapter/endpoint/client call stats,
// error-group trends — and rendering it as one alphabetical table put a 600,000 ms duration SUM next to a count
// of 2 on the same axis, keyed by an assembly-qualified type name. This module is the read-side parser that
// splits the flat list back into the families it was written from, so each one renders with its own units.
//
// Nothing here is authoritative: the key layouts are owned by the `*Keys` classes in Core. An unrecognised key
// is never dropped — it falls through to the `other` family and still renders raw, so an addon that invents its
// own key stays visible.

export interface CounterEntry {
  key: string;
  value: number;
}

export type FamilyId =
  | 'outcomes'
  | 'jobtypes'
  | 'handlers'
  | 'queues'
  | 'deadlines'
  | 'adapters'
  | 'endpoints'
  | 'client'
  | 'issues'
  | 'system'
  | 'other';

export interface ParsedKey {
  family: FamilyId;
  /** The per-application slice (`*-app:` prefixes, §8.23). `null` is the cluster-wide slice, NOT "unknown". */
  application: string | null;
  /** The dimension value the counter is about: job type, queue, adapter, route, fingerprint, … */
  subject: string;
  /** Trailing token — an outcome (`succeeded`/`failed`/`success`), `count`, `dur`, or `pct` for a histogram bucket. */
  token: string;
  /** Upper bound (ms) when `token === 'pct'`, else `null`. */
  bucketMs: number | null;
  /** True for a time-bucketed series key (the `hist:` marker), which only ever reaches the chart. */
  history: boolean;
}

/** The trailing `int.MaxValue` catch-all rung shared by every latency-bucket ladder in Core. */
export const OVERFLOW_BUCKET = 2147483647;

const DURATION_TOKEN = 'dur';
const PCT_TOKEN = 'pct';

// Interprets the segments AFTER the dimension value. Every latency-bearing family (jobstat, qwait, adapter,
// endpoint, deadline) shares this tail shape, which is why it is one function rather than five parsers.
function parseTail(tail: string[]): Pick<ParsedKey, 'token' | 'bucketMs' | 'history'> | null {
  if (tail.length === 1 && tail[0].length > 0) {
    return { token: tail[0], bucketMs: null, history: false };
  }

  if (tail.length === 2 && tail[0] === PCT_TOKEN) {
    const ms = Number(tail[1]);

    return Number.isFinite(ms) ? { token: PCT_TOKEN, bucketMs: ms, history: false } : null;
  }

  // `hist:{token}` — the tier/stamp suffix has already been stripped server-side (MetricTiers, §8.30), so what
  // arrives is the base key. `pcth:` (windowed latency buckets) is filtered out server-side and never lands here.
  if (tail.length === 2 && tail[0] === 'hist' && tail[1].length > 0) {
    return { token: tail[1], bucketMs: null, history: true };
  }

  return null;
}

function dimensional(
  family: FamilyId,
  application: string | null,
  parts: string[],
  from: number,
  subKeys: string[] = [],
): ParsedKey | null {
  let subject = parts[from];
  if (!subject) {
    return null;
  }

  let rest = parts.slice(from + 1);

  // Adapters and endpoints carry an optional second axis (`op:`/`grp:`) between the name and the token. Folding
  // it into the subject keeps one row per real dimension instead of spilling those keys into `other`.
  if (rest.length > 2 && subKeys.includes(rest[0])) {
    subject = `${subject} ${rest[0]}=${rest[1]}`;
    rest = rest.slice(2);
  }

  const tail = parseTail(rest);
  if (!tail) {
    return null;
  }

  return { family, application, subject, ...tail };
}

/**
 * Splits one counter key into the family it belongs to and the dimension it measures. Returns `null` for any
 * key this page does not recognise — the caller renders those raw under `other` rather than discarding them.
 */
export function parseCounterKey(key: string): ParsedKey | null {
  const parts = key.split(':');
  const head = parts[0];

  switch (head) {
    case 'stats':
      // The global job-outcome family. Its own renderer handles the state/reason hierarchy, so the subject is
      // simply the rest of the key.
      return parts.length === 2 && parts[1].length > 0
        ? { family: 'outcomes', application: null, subject: parts[1], token: 'count', bucketMs: null, history: false }
        : null;

    case 'jobstat':
    case 'jobstat-app': {
      const application = head === 'jobstat-app' ? parts[1] : null;
      const at = application === null ? 1 : 2;
      const dimension = parts[at];
      const family: FamilyId | null = dimension === 'type' ? 'jobtypes' : dimension === 'handler' ? 'handlers' : null;

      return family === null ? null : dimensional(family, application, parts, at + 1);
    }

    case 'qwait':
    case 'qwait-app':
      return dimensional('queues', head === 'qwait-app' ? parts[1] : null, parts, head === 'qwait-app' ? 2 : 1);

    // Backlog is a point-in-time gauge, not a fold, and is deliberately never app-sliced (§8.26). It shares the
    // queue subject, so depth/oldest-age land as extra columns on the same row as that queue's wait latency.
    case 'qbacklog':
      return dimensional('queues', null, parts, 1);

    case 'deadline':
    case 'deadline-app':
      return dimensional('deadlines', head === 'deadline-app' ? parts[1] : null, parts, head === 'deadline-app' ? 2 : 1);

    case 'adapter':
    case 'adapter-app':
      return dimensional('adapters', head === 'adapter-app' ? parts[1] : null, parts, head === 'adapter-app' ? 2 : 1, ['op', 'grp']);

    case 'endpoint':
    case 'endpoint-app':
      return dimensional('endpoints', head === 'endpoint-app' ? parts[1] : null, parts, head === 'endpoint-app' ? 2 : 1, ['grp']);

    case 'clientevent':
    case 'clientevent-app':
      return parseClientEvent(parts, head === 'clientevent-app' ? parts[1] : null);

    // Error groups write an hourly trend only, so this family is chart-only — there is no lifetime total row.
    case 'errorgroup':
      return parts.length === 2
        ? { family: 'issues', application: null, subject: parts[1], token: 'count', bucketMs: null, history: true }
        : null;

    case 'errorgroup-app':
      return parts.length === 3
        ? { family: 'issues', application: parts[2], subject: parts[1], token: 'count', bucketMs: null, history: true }
        : null;

    case 'warpsys':
      return parts.length === 3 && parts[1] === 'records-dropped'
        ? { family: 'system', application: null, subject: parts[2], token: 'dropped', bucketMs: null, history: true }
        : null;

    default:
      return null;
  }
}

// Client events use a marker at segment 1 (total / name / vital) instead of a single dimension value, so the
// three shapes are flattened into one prefixed subject rather than three tabs.
function parseClientEvent(parts: string[], application: string | null): ParsedKey | null {
  const at = application === null ? 1 : 2;
  const marker = parts[at];

  if (marker === 'total') {
    const type = parts[at + 1];
    const tail = parts.slice(at + 2);
    if (!type || tail.length !== 1) {
      return null;
    }

    // `…:total:{type}:count` (lifetime) and `…:total:{type}:hist` (series) — the series marker sits where the
    // token sits in every other family, which is why this shape gets its own arm.
    return {
      family: 'client',
      application,
      subject: `type ${type}`,
      token: 'count',
      bucketMs: null,
      history: tail[0] === 'hist',
    };
  }

  if (marker === 'name') {
    const type = parts[at + 1];
    const name = parts[at + 2];

    return type && name && parts.length === at + 4
      ? { family: 'client', application, subject: `${type} ${name}`, token: 'count', bucketMs: null, history: false }
      : null;
  }

  if (marker === 'vital') {
    return dimensional('client', application, parts, at + 1);
  }

  return null;
}

export interface FamilyDef {
  id: FamilyId;
  /**
   * URL segment for /counters/{slug}. Kept separate from `id` so the internal key can be
   * renamed without breaking a link someone shared.
   */
  slug: string;
  label: string;
  description: string;
  /**
   * Tokens that count OBSERVATIONS, i.e. the denominator for `dur ÷ n`. Defaults to every non-duration token,
   * which is correct only where the tokens are disjoint outcomes. Families whose tokens overlap (`miss` ⊂
   * `count`) or mix in gauges (`depth`) must name theirs explicitly or the average is silently wrong.
   */
  countTokens?: string[];
  /**
   * Latency percentiles read off the `pct` histogram, in display order. Web vitals use Google's p75;
   * queues report p95 AND p99, because a queue's tail is the operational signal; everything else p95.
   */
  percentiles?: number[];
  /** Formats the dimension value for display. Falls back to the raw subject. */
  formatSubject?: (subject: string) => { label: string; sub: string | null };
}

// Assembly-qualified names ("Foo.Bar.MyJob, Foo, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null") are what
// Job.Type/HandlerType hold, so that is what the key holds. Showing the raw value made the table unreadable; the
// short name leads and the namespace trails, with the full string on the row title.
function formatTypeSubject(subject: string): { label: string; sub: string | null } {
  const declared = subject.split(',')[0].trim();
  const dot = declared.lastIndexOf('.');

  return { label: shortType(subject), sub: dot > 0 ? declared.slice(0, dot) : null };
}

export const FAMILIES: FamilyDef[] = [
  {
    id: 'outcomes',
    slug: 'job-outcomes',
    label: 'Job outcomes',
    description:
      'Global job-outcome totals and their per-reason breakdown. Recorded events — they only ever increase, so a requeue never rewrites history.',
  },
  {
    id: 'jobtypes',
    slug: 'job-types',
    label: 'Job types',
    description: 'Per-job-type execution counts and latency. One row per published job type.',
    formatSubject: formatTypeSubject,
  },
  {
    id: 'handlers',
    slug: 'handlers',
    label: 'Handlers',
    description: 'The same execution counts sliced by the handler that ran, which differs from the job type for routed messages.',
    formatSubject: formatTypeSubject,
  },
  {
    id: 'queues',
    slug: 'queues',
    label: 'Queues',
    description: 'Queue-wait latency (time a job sat eligible-but-unclaimed) alongside the latest backlog gauge.',
    countTokens: ['count'],
    percentiles: [0.95, 0.99],
  },
  {
    id: 'deadlines',
    slug: 'deadlines',
    label: 'Deadlines',
    description: 'Total-scope timeout attainment per job type — how often a deadline was met versus missed.',
    countTokens: ['count'],
    // The dimension is a job type, same as the two tabs above; without this it rendered the raw
    // assembly-qualified name while they showed the short one.
    formatSubject: formatTypeSubject,
  },
  {
    id: 'adapters',
    slug: 'adapters',
    label: 'Adapters',
    description: 'Outbound service calls per adapter, and per operation or group where those axes were recorded.',
  },
  {
    id: 'endpoints',
    slug: 'endpoints',
    label: 'Endpoints',
    description: 'Inbound calls to Warp HTTP endpoints, keyed by method and route template.',
  },
  {
    id: 'client',
    slug: 'client',
    label: 'Client',
    description: 'Browser events by type and name, plus web-vital measurements. Vitals report p75, the percentile Core Web Vitals is scored on.',
    percentiles: [0.75],
  },
  {
    id: 'issues',
    slug: 'issues',
    label: 'Issues',
    description: 'Hourly occurrence trend per error-group fingerprint. Trend only — the group itself lives on the Issues page.',
    // A fingerprint is 32 hex characters with no information in the tail, so it is truncated. Anything else is
    // a readable identifier and is shown whole — blindly slicing would cut a name in half.
    formatSubject: (subject) => ({
      label: /^[0-9a-f]{16,}$/.test(subject) ? subject.slice(0, 12) : subject,
      sub: null,
    }),
  },
  {
    id: 'system',
    slug: 'system',
    label: 'System',
    description: 'Records dropped by the lossy recording pipelines when their bounded channel was full.',
  },
  {
    id: 'other',
    slug: 'other',
    label: 'Other',
    description: 'Keys this page does not recognise, including any an addon writes itself. Shown raw.',
  },
];

export interface MetricRow {
  /** React key — unique across the family. */
  id: string;
  application: string | null;
  subject: string;
  label: string;
  sub: string | null;
  values: Record<string, number>;
  /**
   * The latency columns are not milliseconds for this row — CLS is a unitless layout-shift
   * score. Rendered as a plain number rather than a duration.
   */
  unitless: boolean;
  avgMs: number | null;
  /** One entry per `FamilyDef.percentiles`, in the same order as `FamilyTable.percentileLabels`. */
  percentiles: RowPercentile[];
}

export interface RowPercentile {
  label: string;
  ms: number | null;
  /** The percentile landed in the catch-all rung, so the real value is only known to be above `ms`. */
  overflow: boolean;
}

export interface FamilyTable {
  columns: string[];
  rows: MetricRow[];
  hasApplication: boolean;
  hasAvg: boolean;
  hasPercentile: boolean;
  percentileLabels: string[];
}

// Counting tokens first (most-load-bearing on the left), then anything a family invented. `dur` and `pct` never
// appear as columns — they are folded into the derived Avg / percentile instead, because a duration SUM in a
// column next to a count is exactly the unit mixing that made the old page unreadable.
const COLUMN_ORDER = ['count', 'succeeded', 'success', 'failed', 'miss', 'throttled', 'circuitopen', 'dropped', 'depth', 'oldest_age_seconds'];

function columnRank(token: string): number {
  const index = COLUMN_ORDER.indexOf(token);

  return index < 0 ? COLUMN_ORDER.length : index;
}

// CLS is unitless and folded x1000 so it fits the shared integer histogram (§8.27). Every other
// latency column genuinely is milliseconds, so the divisor is per-subject rather than per-family.
const UNITLESS_SCALE: Partial<Record<FamilyId, Record<string, number>>> = {
  client: { CLS: 1000 },
};

function percentileLabel(percentile: number): string {
  return `p${Math.round(percentile * 100)}`;
}

export function percentileFromBuckets(buckets: Map<number, number>, percentile: number): { ms: number | null; overflow: boolean } {
  const bounds = [...buckets.keys()].sort((a, b) => a - b);
  const total = bounds.reduce((sum, bound) => sum + (buckets.get(bound) ?? 0), 0);
  if (total === 0) {
    return { ms: null, overflow: false };
  }

  const target = total * percentile;
  let cumulative = 0;

  for (const bound of bounds) {
    cumulative += buckets.get(bound) ?? 0;
    if (cumulative < target) {
      continue;
    }

    if (bound !== OVERFLOW_BUCKET) {
      return { ms: bound, overflow: false };
    }

    // The catch-all rung only says "above the last real bound", so report that bound and flag it rather than
    // rendering int.MaxValue as a latency.
    const highest = bounds.filter((x) => x !== OVERFLOW_BUCKET).pop() ?? null;

    return { ms: highest, overflow: true };
  }

  return { ms: bounds[bounds.length - 1] ?? null, overflow: false };
}

/**
 * Pivots the flat key list into one row per (application, dimension) for a single family, with the duration sum
 * and latency histogram collapsed into derived Avg / percentile columns.
 */
export function buildFamilyTable(entries: CounterEntry[], family: FamilyDef): FamilyTable {
  const rows = new Map<string, MetricRow & { buckets: Map<number, number> }>();
  const columns = new Set<string>();
  let hasApplication = false;
  let hasAvg = false;
  let hasPercentile = false;

  for (const entry of entries) {
    const parsed = parseCounterKey(entry.key);
    if (!parsed || parsed.family !== family.id || parsed.history) {
      continue;
    }

    const id = `${parsed.application ?? ''} ${parsed.subject}`;
    let row = rows.get(id);
    if (!row) {
      const formatted = family.formatSubject?.(parsed.subject) ?? { label: parsed.subject, sub: null };
      row = {
        id,
        application: parsed.application,
        subject: parsed.subject,
        label: formatted.label,
        sub: formatted.sub,
        values: {},
        unitless: false,
        avgMs: null,
        percentiles: [],
        buckets: new Map(),
      };
      rows.set(id, row);
    }

    hasApplication = hasApplication || parsed.application !== null;

    if (parsed.token === PCT_TOKEN && parsed.bucketMs !== null) {
      row.buckets.set(parsed.bucketMs, (row.buckets.get(parsed.bucketMs) ?? 0) + entry.value);
      hasPercentile = true;
      continue;
    }

    row.values[parsed.token] = (row.values[parsed.token] ?? 0) + entry.value;

    if (parsed.token === DURATION_TOKEN) {
      hasAvg = true;
      continue;
    }

    columns.add(parsed.token);
  }

  const percentiles = family.percentiles ?? [0.95];

  for (const row of rows.values()) {
    const denominator = (family.countTokens ?? [...columns]).reduce((sum, token) => sum + (row.values[token] ?? 0), 0);
    const duration = row.values[DURATION_TOKEN];
    row.avgMs = duration !== undefined && denominator > 0 ? duration / denominator : null;

    row.percentiles = percentiles.map((q) => {
      const p = percentileFromBuckets(row.buckets, q);

      return { label: percentileLabel(q), ms: p.ms, overflow: p.overflow };
    });

    const scale = UNITLESS_SCALE[family.id]?.[row.subject];
    if (scale !== undefined) {
      row.unitless = true;
      row.avgMs = row.avgMs === null ? null : row.avgMs / scale;
      row.percentiles = row.percentiles.map((x) => ({ ...x, ms: x.ms === null ? null : x.ms / scale }));
    }
  }

  const ordered = [...columns].sort((a, b) => columnRank(a) - columnRank(b) || a.localeCompare(b));
  const primary = ordered[0];

  return {
    columns: ordered,
    rows: [...rows.values()].sort(
      (a, b) => (b.values[primary] ?? 0) - (a.values[primary] ?? 0) || a.label.localeCompare(b.label),
    ),
    hasApplication,
    hasAvg,
    hasPercentile,
    percentileLabels: percentiles.map(percentileLabel),
  };
}

export interface CounterRow {
  /** React key. Unique per row — derived rows namespace themselves under the group they belong to. */
  key: string;
  /** What the cell shows. Equals `key` for a real counter row. */
  label: string;
  value: number;
  depth: number;
  muted?: boolean;
  warn?: boolean;
}

// The outcome family is a hierarchy, not an alphabetical list: state totals with a reason breakdown under
// each. Rendering it flat forced the reader to reconstruct that from key names.
//
// ONLY failed and deleted nest under the unsuccessful umbrella. A success is obviously not an unsuccessful
// outcome, and a requeue is not even a terminal one — the same job will run again and land in one of the
// other three totals, so indenting either under the umbrella claims something false.
const UMBRELLA_KEY = 'stats:unsuccessful';

// `attributable` marks the states that HAVE a reason taxonomy. Succeeded does not — nothing stamps a reason
// on a success — so it is the one total whose missing breakdown is expected rather than informative.
const OUTCOME_GROUPS: { total: string; underUmbrella: boolean; attributable: boolean }[] = [
  { total: 'stats:succeeded', underUmbrella: false, attributable: false },
  { total: 'stats:failed', underUmbrella: true, attributable: true },
  { total: 'stats:deleted', underUmbrella: true, attributable: true },
  { total: 'stats:requeued', underUmbrella: false, attributable: true },
];

/**
 * Builds the `stats:` outcome hierarchy: state totals, their per-reason children, and the derived umbrella.
 * Also returns any `stats:` key that matched no group so a newly-added outcome key can never vanish.
 */
export function buildOutcomeRows(counters: CounterEntry[]): CounterRow[] {
  const byKey = new Map(counters.map((c) => [c.key, c.value]));
  const claimed = new Set<string>();
  const outcomes: CounterRow[] = [];

  // Derived on read, never stored. "Not Completed" is exactly failed + deleted, and ten sites write those
  // two keys (worker cancellation, DeleteJob, BulkDelete, crash recovery, …). A stored umbrella has to be
  // maintained at every one of them or it silently under-reports — which is precisely what it did. Computing
  // it here cannot drift from the totals it sums.
  const failed = byKey.get('stats:failed');
  const deleted = byKey.get('stats:deleted');
  const umbrella = failed === undefined && deleted === undefined ? undefined : (failed ?? 0) + (deleted ?? 0);

  // Claimed so a leftover row from a build that still wrote it doesn't render twice with two values.
  claimed.add(UMBRELLA_KEY);

  let umbrellaEmitted = false;

  for (const group of OUTCOME_GROUPS) {
    const total = byKey.get(group.total);
    if (total === undefined) continue;

    if (group.underUmbrella && umbrella !== undefined && !umbrellaEmitted) {
      outcomes.push({ key: UMBRELLA_KEY, label: `${UMBRELLA_KEY} (derived: failed + deleted)`, value: umbrella, depth: 0 });
      umbrellaEmitted = true;
    }

    const depth = group.underUmbrella && umbrella !== undefined ? 1 : 0;
    outcomes.push({ key: group.total, label: group.total, value: total, depth });
    claimed.add(group.total);

    const reasons = counters
      .filter((c) => c.key.startsWith(group.total + '-'))
      .sort((a, b) => b.value - a.value);

    let attributed = 0;
    for (const reason of reasons) {
      outcomes.push({ key: reason.key, label: reason.key, value: reason.value, depth: depth + 1 });
      claimed.add(reason.key);
      attributed += reason.value;
    }

    // "Unattributed" is computed and SHOWN rather than hidden. An outcome with no attributable cause (a
    // plain handler throw with no addon involved) carries no reason, so a state total is legitimately
    // larger than the sum of its reasons. Naming the remainder beats letting someone conclude the numbers
    // are broken. The row key is namespaced by group — two groups with a remainder used to emit the same
    // React key twice.
    //
    // Gated on the group being attributable at all, NOT on some reasons having arrived. A deployment whose
    // failures are all plain handler throws has zero reason rows and a fully unattributed total — the case
    // the remainder most needs to explain — and hiding it there would show a bare total on exactly the
    // page that promises the breakdown. Succeeded is excluded because it has no reason taxonomy to be
    // missing from.
    if (group.attributable && total > attributed) {
      outcomes.push({
        key: `${group.total}#unattributed`,
        label: `unattributed (${group.total})`,
        value: total - attributed,
        depth: depth + 1,
        muted: true,
      });
    }

    // The impossible direction, surfaced rather than swallowed: a child larger than its parent means a
    // reason key was written without its state total (a write site out of step). Hiding it would render a
    // breakdown that visibly does not add up with no explanation on screen.
    if (attributed > total) {
      outcomes.push({
        key: `${group.total}#over-attributed`,
        label: `over-attributed (${group.total}) — reasons exceed the total`,
        value: attributed - total,
        depth: depth + 1,
        warn: true,
      });
    }
  }

  const distinct = byKey.get('stats:retried-jobs');
  if (distinct !== undefined) {
    outcomes.push({ key: 'stats:retried-jobs', label: 'stats:retried-jobs', value: distinct, depth: 0 });
    claimed.add('stats:retried-jobs');
  }

  // Any stats: key no group claimed. A new outcome key added in Core must still show up here rather than
  // silently disappearing because this page has not been taught about it yet.
  for (const counter of counters) {
    if (!claimed.has(counter.key)) {
      outcomes.push({ key: counter.key, label: counter.key, value: counter.value, depth: 0 });
    }
  }

  return outcomes;
}

export interface HistoryPointLike {
  hour: string;
  key: string;
  value: number;
}

export interface FamilySeries {
  id: string;
  label: string;
  /** What `colorFor` hashes, so the built-in outcome palette still matches by full key. */
  colorKey: string;
  total: number;
  byHour: Map<number, number>;
}

/**
 * The metrics a family's history actually contains, most-load-bearing first. The chart plots ONE of these at a
 * time: a duration SUM and an execution count share no axis, and plotting them together is what reduced the old
 * single chart to one 600,000 ms curve with everything else flat on zero.
 */
export function historyTokens(points: HistoryPointLike[], familyId: FamilyId): string[] {
  const tokens = new Set<string>();

  for (const point of points) {
    // No `history` check: everything in this list IS a series. The flag only exists to keep series keys out of
    // the lifetime table, and the `stats:` family reuses one key shape for both.
    const parsed = parseCounterKey(point.key);
    if (parsed?.family === familyId) {
      tokens.add(parsed.token);
    }
  }

  return [...tokens].sort((a, b) => columnRank(a) - columnRank(b) || a.localeCompare(b));
}

/** One series per dimension value for a single metric, largest total first. */
export function buildFamilySeries(points: HistoryPointLike[], family: FamilyDef, token: string): FamilySeries[] {
  const series = new Map<string, FamilySeries>();

  for (const point of points) {
    const parsed = parseCounterKey(point.key);
    if (!parsed || parsed.family !== family.id || parsed.token !== token) {
      continue;
    }

    const id = `${parsed.application ?? ''} ${parsed.subject}`;
    let entry = series.get(id);
    if (!entry) {
      const formatted = family.formatSubject?.(parsed.subject) ?? { label: parsed.subject, sub: null };
      const suffix = parsed.application === null ? '' : ` [${parsed.application}]`;
      entry = {
        id,
        label: `${formatted.label}${suffix}`,
        colorKey: family.id === 'outcomes' ? point.key : id,
        total: 0,
        byHour: new Map(),
      };
      series.set(id, entry);
    }

    const hourMs = new Date(point.hour).getTime();
    entry.byHour.set(hourMs, (entry.byHour.get(hourMs) ?? 0) + point.value);
    entry.total += point.value;
  }

  return [...series.values()].sort((a, b) => b.total - a.total || a.label.localeCompare(b.label));
}

/** The families that have at least one counter or one history series, in display order. */
export function familyBySlug(slug: string | undefined): FamilyDef | undefined {
  return FAMILIES.find((x) => x.slug === slug);
}

export function presentFamilies(counters: CounterEntry[], historyKeys: string[]): FamilyDef[] {
  const present = new Set<FamilyId>();

  for (const counter of counters) {
    present.add(parseCounterKey(counter.key)?.family ?? 'other');
  }

  for (const key of historyKeys) {
    present.add(parseCounterKey(key)?.family ?? 'other');
  }

  return FAMILIES.filter((f) => present.has(f.id));
}
