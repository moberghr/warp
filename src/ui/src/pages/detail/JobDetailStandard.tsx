import { Fragment, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Copy, RotateCw, Trash2, ExternalLink, X } from 'lucide-react';
import { shortType, shortId, stateName, formatDateTime, formatDuration, detailPath } from '@/utils/format';
import { State } from '@/types';
import type { UnifiedJobDetailModel, JobLogModel, ContinuationInfo } from '@/types';
import { useDeleteJob, useRequeueJob } from '@/api/hooks/useJobs';
import { useConfirm } from '@/components/forms/useConfirm';
import { RelativeTime } from '@/components/RelativeTime';
import { StateBadge } from '@/components/StateBadge';
import { RelatedJobsSection } from './RelatedJobsSection';

// ============================================================================
// A·Soft job detail — pixel port of warp/project/soft/job-detail.jsx
// ============================================================================

function kindLabel(kind: number) {
  if (kind === 3) return 'Batch';
  if (kind === 2) return 'Message';

  return 'Job';
}

function pillClassForState(state: State): string {
  switch (state) {
    case State.Awaiting:    return 'awaiting';
    case State.Scheduled:   return 'scheduled';
    case State.Enqueued:    return 'enqueued';
    case State.Processing:  return 'processing';
    case State.Completed:   return 'completed';
    case State.Failed:      return 'failed';
    case State.Deleted:     return 'deleted';
    default:                return 'deleted';
  }
}

function eventStateClass(eventType: string): string {
  switch (eventType) {
    case 'Created':    return 'enqueued';
    case 'Enqueued':   return 'enqueued';
    case 'Scheduled':  return 'scheduled';
    case 'Processing': return 'processing';
    case 'Completed':  return 'completed';
    case 'Failed':     return 'failed';
    case 'Deleted':    return 'deleted';
    case 'Requeued':
    case 'Retried':    return 'awaiting';
    default:           return 'enqueued';
  }
}

function eventStateVar(eventType: string): string {
  const cls = eventStateClass(eventType);
  if (cls === 'enqueued')   return 'var(--state-enqueued)';
  if (cls === 'scheduled')  return 'var(--state-scheduled)';
  if (cls === 'awaiting')   return 'var(--state-awaiting)';
  if (cls === 'processing') return 'var(--state-processing)';
  if (cls === 'completed')  return 'var(--state-completed)';
  if (cls === 'failed')     return 'var(--state-failed)';
  return 'var(--state-deleted)';
}

function eventStateBgVar(eventType: string): string {
  const cls = eventStateClass(eventType);
  if (cls === 'enqueued')   return 'var(--state-enqueued-bg)';
  if (cls === 'scheduled')  return 'var(--state-scheduled-bg)';
  if (cls === 'awaiting')   return 'var(--state-awaiting-bg)';
  if (cls === 'processing') return 'var(--state-processing-bg)';
  if (cls === 'completed')  return 'var(--state-completed-bg)';
  if (cls === 'failed')     return 'var(--state-failed-bg)';
  return 'var(--state-deleted-bg)';
}

function getDuration(logs: JobLogModel[], idx: number): string | null {
  const log = logs[idx];
  if (log.durationMs != null) return formatDuration(log.durationMs);
  if (idx >= logs.length - 1) return null;
  const current = new Date(logs[idx].timestamp).getTime();
  const previous = new Date(logs[idx + 1].timestamp).getTime();

  return formatDuration(current - previous);
}

function formatJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

// JSON syntax-coloring per the design: keys green, strings amber, numbers purple.
function tokenizeJsonLine(line: string): ReactNode {
  const kv = line.match(/^(\s*)("[^"]+")(:\s*)(.+?)(,?\s*)$/);
  if (kv) {
    const [, ind, key, sep, val, tail] = kv;
    const trimmed = val.trim();
    let valEl: ReactNode;
    if (val.startsWith('"')) {
      valEl = <span style={{ color: 'var(--warp-amber)' }}>{val}</span>;
    } else if (/^(true|false|null)$/.test(trimmed)) {
      valEl = <span style={{ color: 'var(--warp-amber)' }}>{val}</span>;
    } else if (/^-?\d/.test(trimmed)) {
      valEl = <span style={{ color: 'var(--warp-purple)' }}>{val}</span>;
    } else {
      valEl = <span>{val}</span>;
    }

    return (
      <>
        {ind}
        <span style={{ color: 'var(--warp-green)' }}>{key}</span>
        <span style={{ color: 'var(--text-mute)' }}>{sep}</span>
        {valEl}
        {tail}
      </>
    );
  }
  const num = line.match(/^(\s*)(-?\d+(?:\.\d+)?)(,?)$/);
  if (num) {
    const [, ind, n, tail] = num;

    return (
      <>
        {ind}
        <span style={{ color: 'var(--warp-purple)' }}>{n}</span>
        {tail}
      </>
    );
  }

  return <span style={{ color: 'var(--text-dim)' }}>{line}</span>;
}

// ----- TitleStrip -----
interface TitleStripProps {
  job: UnifiedJobDetailModel;
  onRequeue: () => void;
  onDelete: () => void;
  isRequeuing: boolean;
  isDeleting: boolean;
}

function TitleStrip({ job, onRequeue, onDelete, isRequeuing, isDeleting }: TitleStripProps) {
  const kind = kindLabel(job.kind);
  const pill = pillClassForState(job.currentState);
  const stateLabel = stateName(job.currentState);
  const isFailed = job.currentState === State.Failed;
  const isProcessing = job.currentState === State.Processing;
  const isJob = job.kind === 1;
  const isMessage = job.kind === 2;
  const showActions = isJob || isMessage;
  const totalAttempts = job.maxRetries + 1;
  const currentAttempt = job.retriedTimes + 1;
  const hasRetryPolicy = job.maxRetries > 0;
  const attemptsLabel = `${currentAttempt} / ${totalAttempts}`;

  return (
    <div style={{ padding: '16px 0 16px', borderBottom: '1px solid var(--hair)' }}>
      <div className="flex items-end justify-between gap-6 flex-wrap">
        <div className="min-w-0">
          <div className="flex items-center gap-3.5 flex-wrap">
            <span
              className="font-semibold text-foreground"
              style={{ fontSize: 32, letterSpacing: '-0.6px', lineHeight: 1 }}
            >
              {kind}
            </span>
            <span
              className="mono font-medium text-foreground tabular-nums"
              style={{ fontSize: 32, letterSpacing: '-0.6px', lineHeight: 1 }}
            >
              {job.id.slice(0, 8)}
            </span>
            <span className={`soft-pill ${pill}`}>{stateLabel}</span>
            {job.type && (
              <span className="inline-flex items-baseline gap-1.5" style={{ fontSize: 14 }}>
                <span className="soft-eyebrow">Request</span>
                <span className="text-text-dim font-medium">{shortType(job.type)}</span>
              </span>
            )}
            {job.handlerType && (
              <span className="inline-flex items-baseline gap-1.5" style={{ fontSize: 14 }}>
                <span className="soft-eyebrow">Handler</span>
                <span className="text-text-dim font-medium">{shortType(job.handlerType)}</span>
              </span>
            )}
          </div>
          {job.parentJob && (
            <div className="mt-3">
              <Link
                to={detailPath(job.parentJob.id, job.parentJob.kind)}
                className="inline-flex items-center gap-2 rounded-full px-2.5 py-1 hover:opacity-90 transition-opacity"
                style={{
                  background: 'var(--brand-wash)',
                  border: '1px solid color-mix(in srgb, var(--brand) 20%, transparent)',
                  color: 'var(--brand)',
                  fontSize: 12,
                  fontWeight: 600,
                }}
              >
                <span className="soft-eyebrow" style={{ color: 'var(--brand)' }}>
                  Part of
                </span>
                <span className="mono">
                  {kindLabel(job.parentJob.kind).toLowerCase()} {job.parentJob.id.slice(0, 8)}
                </span>
                {job.parentJob.type && (
                  <span
                    className="font-medium"
                    style={{ color: 'var(--text-dim)' }}
                  >
                    · {shortType(job.parentJob.type)}
                  </span>
                )}
              </Link>
            </div>
          )}
        </div>

        {showActions && (
          <div className="flex items-center gap-2 shrink-0">
            {isJob && isProcessing ? (
              <button
                type="button"
                onClick={onDelete}
                disabled={isDeleting}
                className="soft-btn soft-btn-danger"
              >
                <X size={14} /> Cancel
              </button>
            ) : (
              <>
                <button
                  type="button"
                  onClick={onRequeue}
                  disabled={isRequeuing}
                  className={`soft-btn ${isFailed ? 'soft-btn-primary' : 'soft-btn-dark'}`}
                >
                  <RotateCw size={14} /> Requeue
                </button>
                <button
                  type="button"
                  onClick={onDelete}
                  disabled={isDeleting}
                  className="soft-btn soft-btn-danger"
                >
                  <Trash2 size={14} /> Delete
                </button>
              </>
            )}
            {job.traceId && (
              <Link to={`/trace/${job.traceId}`} className="soft-btn soft-btn-ghost">
                <ExternalLink size={14} /> Trace
              </Link>
            )}
          </div>
        )}
      </div>

      <div className="mt-4 flex flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-7 sm:gap-y-2 min-w-0">
        {([
          ['Created', formatDateTime(job.createTime), false],
          ['Queue', job.queue ?? 'default', false],
          job.scheduleTime ? ['Scheduled', formatDateTime(job.scheduleTime), false] : null,
          hasRetryPolicy ? ['Attempt', attemptsLabel, false] : null,
          ['ID', job.id, true],
          job.traceId ? ['Trace', job.traceId, true] : null,
        ].filter(Boolean) as Array<[string, string, boolean]>).map((row) => {
          const [k, v, copy] = row;

          return (
            <div
              key={k}
              className="flex items-baseline gap-2.5 min-w-0"
            >
              <span className="soft-eyebrow shrink-0">{k}</span>
              <span
                className="mono text-foreground break-all"
                style={{ fontSize: 12.5, letterSpacing: 0.2 }}
              >
                {v}
              </span>
              {copy && (
                <button
                  type="button"
                  onClick={() => void navigator.clipboard?.writeText(v)}
                  className="text-text-mute hover:text-foreground shrink-0"
                  aria-label={`Copy ${k}`}
                >
                  <Copy size={12} />
                </button>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ----- Section -----
function Section({
  label,
  action,
  children,
  noBorder,
}: {
  label: string;
  action?: ReactNode;
  children: ReactNode;
  noBorder?: boolean;
}) {
  return (
    <div
      style={{
        padding: '22px 0 22px',
        borderBottom: noBorder ? 'none' : '1px solid var(--hair)',
      }}
    >
      <div className="mb-3.5 flex items-center justify-between gap-3">
        <span className="soft-eyebrow">{label}</span>
        {action && <span>{action}</span>}
      </div>
      {children}
    </div>
  );
}

// ----- JSON block -----
function JsonBlock({ text }: { text: string }) {
  return (
    <pre
      className="mono m-0"
      style={{
        fontSize: 13,
        lineHeight: 1.85,
        color: 'var(--text-dim)',
        whiteSpace: 'pre',
        overflowX: 'auto',
      }}
    >
      {text.split('\n').map((l, i) => (
        <div key={i}>{tokenizeJsonLine(l)}</div>
      ))}
    </pre>
  );
}

// ----- Lifecycle (flush card of state-colored entries) -----
function LifecycleCard({ events, jobId }: { events: JobLogModel[]; jobId: string }) {
  if (events.length === 0) return null;

  return (
    <div
      data-warp-slot="detail.history"
      data-warp-context={JSON.stringify({ jobId })}
      className="soft-card flush"
      key={`lc-${jobId}`}
    >
      {events.map((ev, i) => {
        const isLast = i === events.length - 1;
        const dur = getDuration(events, i);
        const color = eventStateVar(ev.eventType);
        const bg = eventStateBgVar(ev.eventType);

        return (
          <div
            key={ev.id}
            className="relative"
            style={{
              padding: '14px 18px 16px 22px',
              borderBottom: isLast ? 'none' : '1px solid var(--hair-soft)',
            }}
          >
            <span
              aria-hidden
              className="absolute"
              style={{
                left: 0,
                top: 14,
                bottom: 16,
                width: 3,
                background: color,
                borderRadius: 2,
              }}
            />
            <div className="flex items-baseline justify-between gap-3 flex-wrap">
              <span
                className="mono"
                style={{
                  fontSize: 10.5,
                  color,
                  fontWeight: 700,
                  letterSpacing: '1.6px',
                  textTransform: 'uppercase',
                }}
              >
                {ev.eventType}
              </span>
              <span
                className="mono inline-flex items-center gap-2"
                style={{ fontSize: 11, color: 'var(--text-mute)' }}
              >
                <span style={{ color: 'var(--text-dim)' }}>
                  {formatDateTime(ev.timestamp)}
                </span>
                <span style={{ opacity: 0.5 }}>·</span>
                <span>
                  <RelativeTime date={ev.timestamp} />
                </span>
                {dur && (
                  <span
                    className="font-semibold"
                    style={{
                      padding: '1px 6px',
                      borderRadius: 4,
                      background: bg,
                      color,
                    }}
                  >
                    {dur}
                  </span>
                )}
              </span>
            </div>
            {ev.message && (
              <div className="mt-2 text-text-dim" style={{ fontSize: 13, lineHeight: 1.5 }}>
                {ev.message}
              </div>
            )}
            {ev.exception && (
              <pre
                className="mono mt-2 overflow-auto rounded-md"
                style={{
                  background: 'var(--state-failed-bg)',
                  color: 'var(--state-failed)',
                  padding: '10px 12px',
                  fontSize: 11.5,
                  lineHeight: 1.5,
                  maxHeight: 240,
                  whiteSpace: 'pre',
                }}
              >
                {ev.exception}
              </pre>
            )}
          </div>
        );
      })}
    </div>
  );
}

// ----- Reported progress bars -----
function ReportedProgressSection({ bars }: { bars: Array<[string, number]> }) {
  if (bars.length === 0) return null;

  return (
    <Section label={`Reported progress · ${bars.length}`}>
      <div className="flex flex-col gap-3">
        {bars.map(([name, value]) => (
          <div key={name}>
            <div className="flex items-baseline justify-between mb-1.5">
              <span className="mono" style={{ fontSize: 11.5, color: 'var(--text-dim)' }}>
                {name === '' ? 'Progress' : name}
              </span>
              <span className="mono font-medium tabular-nums" style={{ fontSize: 11.5, color: 'var(--foreground)' }}>
                {value}%
              </span>
            </div>
            <div
              style={{
                height: 6,
                background: 'var(--hair)',
                borderRadius: 999,
                overflow: 'hidden',
              }}
            >
              <div
                style={{
                  height: '100%',
                  width: `${Math.min(100, Math.max(0, value))}%`,
                  background: 'var(--brand)',
                  transition: 'width 200ms ease',
                }}
              />
            </div>
          </div>
        ))}
      </div>
    </Section>
  );
}

// ----- Batch / Message progress (completed + failed split) -----
function BatchProgressSection({
  total,
  completed,
  failed,
}: {
  total: number;
  completed: number;
  failed: number;
}) {
  if (total <= 0) return null;
  const done = completed + failed;
  const pct = Math.round((done / total) * 100);
  const greenPct = (completed / total) * 100;
  const redPct = (failed / total) * 100;

  return (
    <Section label="Progress">
      <div className="flex items-center gap-4">
        <div
          className="flex-1"
          style={{
            height: 8,
            background: 'var(--hair)',
            borderRadius: 999,
            overflow: 'hidden',
            display: 'flex',
          }}
        >
          {greenPct > 0 && (
            <div style={{ height: '100%', width: `${greenPct}%`, background: 'var(--state-completed)' }} />
          )}
          {redPct > 0 && (
            <div style={{ height: '100%', width: `${redPct}%`, background: 'var(--state-failed)' }} />
          )}
        </div>
        <span className="mono font-medium tabular-nums" style={{ fontSize: 12.5, color: 'var(--foreground)' }}>
          {done}/{total} ({pct}%)
        </span>
      </div>
    </Section>
  );
}

// ----- Flow (parent / spawned-by / continuations / spawned jobs) -----
function FlowSection({ job }: { job: UnifiedJobDetailModel }) {
  const hasAnything =
    job.parentJob ||
    job.spawnedByJob ||
    (job.continuations && job.continuations.length > 0) ||
    (job.spawnedJobs && job.spawnedJobs.length > 0);
  if (!hasAnything) return null;

  const kindWord = (k: number | null | undefined) => {
    if (k === 3) return 'Batch';
    if (k === 2) return 'Message';

    return 'Job';
  };

  const renderRow = (item: ContinuationInfo) => (
    <div
      key={item.id}
      className="grid items-center gap-3"
      style={{
        gridTemplateColumns: '100px 1fr 1fr 80px auto',
        padding: '8px 0',
        borderBottom: '1px solid var(--hair-soft)',
        fontSize: 12.5,
      }}
    >
      <Link to={detailPath(item.id, item.kind)} className="mono" style={{ color: 'var(--brand)' }}>
        {shortId(item.id)}
      </Link>
      <span className="mono truncate" style={{ color: 'var(--foreground)' }}>
        {shortType(item.type)}
      </span>
      <span className="mono truncate" style={{ color: 'var(--text-dim)' }}>
        {item.handlerType ? shortType(item.handlerType) : '—'}
      </span>
      <span className="mono" style={{ fontSize: 11, color: 'var(--text-mute)' }}>
        {kindWord(item.kind)}
      </span>
      <StateBadge state={item.currentState} />
    </div>
  );

  const renderGroup = (label: string, items: ContinuationInfo[]) => {
    if (items.length === 0) return null;

    return (
      <div key={label} style={{ marginTop: 6 }}>
        <div className="soft-eyebrow" style={{ marginBottom: 4 }}>
          {label} · {items.length}
        </div>
        {items.map(renderRow)}
      </div>
    );
  };

  return (
    <Section label="Flow">
      <div className="flex flex-col gap-1">
        {job.parentJob && renderGroup('Parent', [job.parentJob])}
        {job.spawnedByJob && renderGroup('Spawned by', [job.spawnedByJob])}
        {job.continuations && job.continuations.length > 0 && renderGroup('Continuations', job.continuations)}
        {job.spawnedJobs && job.spawnedJobs.length > 0 && renderGroup('Spawned jobs', job.spawnedJobs)}
      </div>
    </Section>
  );
}

// ----- Handler output (flush card, mono grid) -----
function HandlerOutputCard({ logs, jobId }: { logs: JobLogModel[]; jobId: string }) {
  if (logs.length === 0) return null;

  const levelColor = (level: string): string => {
    const l = level.toLowerCase();
    if (l === 'error' || l === 'critical' || l === 'fatal') return 'var(--warp-red)';
    if (l === 'warning' || l === 'warn') return 'var(--warp-amber)';
    if (l === 'debug' || l === 'trace') return 'var(--text-mute)';

    return 'var(--warp-blue)';
  };

  return (
    <div data-warp-slot="detail.logs" data-warp-context={JSON.stringify({ jobId })}>
      <div className="mb-3 flex items-center gap-2">
        <span className="soft-eyebrow">Handler output</span>
        <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-mute)' }}>
          · {logs.length}
        </span>
      </div>
      <div className="soft-card flush">
        {logs.map((log, i) => {
          const color = levelColor(log.level);
          const isLast = i === logs.length - 1;

          return (
            <div
              key={log.id}
              className="mono flex flex-col gap-1.5 sm:grid sm:items-baseline sm:gap-3 sm:[grid-template-columns:170px_92px_1fr]"
              style={{
                padding: '12px 14px',
                borderBottom: isLast ? 'none' : '1px solid var(--hair-soft)',
                fontSize: 11.5,
                lineHeight: 1.5,
              }}
            >
              <div className="flex items-center gap-2 sm:contents">
                <span style={{ color: 'var(--text-mute)', letterSpacing: 0.3 }}>
                  {formatDateTime(log.timestamp)}
                </span>
                <span
                  className="font-bold uppercase text-center"
                  style={{
                    fontSize: 9.5,
                    letterSpacing: '1.2px',
                    color,
                    background: `color-mix(in srgb, ${color} 12%, transparent)`,
                    border: `1px solid color-mix(in srgb, ${color} 18%, transparent)`,
                    borderRadius: 4,
                    padding: '1px 7px',
                    width: 'fit-content',
                  }}
                >
                  {log.level}
                </span>
              </div>
              <span style={{ color: 'var(--text-dim)' }} className="break-words min-w-0">
                <span style={{ color: color, fontWeight: log.level.toLowerCase() === 'error' ? 600 : 400 }}>
                  {log.message}
                </span>
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ============================================================================
// Page
// ============================================================================

interface JobDetailStandardProps {
  job: UnifiedJobDetailModel;
  systemEvents: JobLogModel[];
  handlerLogs: JobLogModel[];
  reportedBars: Array<[string, number]>;
  jobCounts: Record<string, number>;
  onCountsUpdate: (counts: Record<string, number>) => void;
}

export function JobDetailStandard({
  job,
  systemEvents,
  handlerLogs,
  reportedBars,
  jobCounts,
  onCountsUpdate,
}: JobDetailStandardProps) {
  const requeue = useRequeueJob();
  const deleteJob = useDeleteJob();
  const { confirm, dialog: confirmDialog } = useConfirm();

  const askRequeue = async () => {
    const ok = await confirm({
      title: 'Requeue job?',
      description: 'The job will be re-enqueued and picked up by a worker on the next poll.',
      confirmLabel: 'Requeue',
    });
    if (ok) {
      requeue.mutate(job.id);
    }
  };

  const totalJobs = Object.keys(jobCounts).length > 0
    ? Object.values(jobCounts).reduce((a, b) => a + b, 0)
    : job.totalJobs;
  const completedJobs = jobCounts['completed'] ?? job.completedJobs;
  const failedJobs = jobCounts['failed'] ?? job.failedJobs;

  const isMessage = job.kind === 2;

  const askDelete = async (action: 'cancel' | 'delete') => {
    const ok = await confirm({
      title: action === 'cancel' ? 'Cancel running job?' : 'Delete job?',
      description:
        action === 'cancel'
          ? `Request graceful cancellation of ${job.id.slice(0, 8)}. The handler may still complete if it ignores the cancellation token.`
          : `Delete ${job.id.slice(0, 8)}? This cannot be undone.`,
      confirmLabel: action === 'cancel' ? 'Cancel job' : 'Delete',
      destructive: true,
    });
    if (ok) {
      deleteJob.mutate(job.id);
    }
  };

  const hasMessage = !!job.message;
  const messageJson = hasMessage ? formatJson(job.message!) : '';
  const metaEntries = job.metadata ? Object.entries(job.metadata) : [];
  const hasMetadata = metaEntries.length > 0;
  const metadataJson = hasMetadata
    ? JSON.stringify(Object.fromEntries(metaEntries), null, 2)
    : '';

  const eventCount = systemEvents.length;

  return (
    <Fragment>
      <TitleStrip
        job={job}
        onRequeue={() => void askRequeue()}
        onDelete={() =>
          askDelete(
            job.kind === 1 && job.currentState === State.Processing
              ? 'cancel'
              : 'delete',
          )
        }
        isRequeuing={requeue.isPending}
        isDeleting={deleteJob.isPending}
      />

      <div className="grid grid-cols-1 gap-x-12 gap-y-0 lg:[grid-template-columns:minmax(0,1.05fr)_minmax(0,1fr)]">
        {/* LEFT */}
        <div className="min-w-0">
          <Section label="Payload">
            {hasMessage ? (
              <JsonBlock text={messageJson} />
            ) : (
              <span
                className="mono"
                style={{ fontSize: 24, color: 'var(--ink-light)', lineHeight: 1 }}
              >
                {'{ }'}
              </span>
            )}
          </Section>

          <Section
            label="Metadata"
            action={
              <span
                className="mono"
                style={{ fontSize: 11, color: 'var(--text-mute)' }}
              >
                {metaEntries.length} {metaEntries.length === 1 ? 'key' : 'keys'}
              </span>
            }
          >
            {hasMetadata ? (
              <JsonBlock text={metadataJson} />
            ) : (
              <span
                className="mono"
                style={{ fontSize: 24, color: 'var(--ink-light)', lineHeight: 1 }}
              >
                {'{ }'}
              </span>
            )}
          </Section>

          <BatchProgressSection total={totalJobs} completed={completedJobs} failed={failedJobs} />

          <ReportedProgressSection bars={reportedBars} />

          <FlowSection job={job} />

        </div>

        {/* RIGHT */}
        <div className="min-w-0 lg:pt-[22px]">
          <div className="mb-6">
            <div className="mb-3 flex items-center gap-2">
              <span className="soft-eyebrow">Lifecycle</span>
              <span
                className="mono"
                style={{ fontSize: 10.5, color: 'var(--text-mute)' }}
              >
                · {eventCount} {eventCount === 1 ? 'event' : 'events'}
              </span>
            </div>
            <LifecycleCard events={systemEvents} jobId={job.id} />
          </div>

          <HandlerOutputCard logs={handlerLogs} jobId={job.id} />
        </div>
      </div>

      {isMessage && (
        <RelatedJobsSection job={job} onCountsUpdate={onCountsUpdate} />
      )}

      {confirmDialog}
    </Fragment>
  );
}
