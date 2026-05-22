import { Repeat, ExternalLink, Trash2 } from 'lucide-react';
import { Link } from 'react-router-dom';
import { Panel, PanelHeader, Eyebrow } from '@/components/v2/Panel';
import { shortId, shortType } from '@/utils/format';
import { useDeleteJob, useRequeueJob } from '@/api/hooks/useJobs';
import type { UnifiedJobDetailModel, JobLogModel } from '@/types';
import { JobLogs } from './JobLogs';

function formatDuration(ms: number | null | undefined): string | null {
  if (ms == null) {
    return null;
  }
  if (ms < 1000) {
    return `${Math.round(ms)}ms`;
  }
  if (ms < 60000) {
    return `${(ms / 1000).toFixed(2)}s`;
  }
  const mins = Math.floor(ms / 60000);
  const secs = Math.floor((ms % 60000) / 1000);

  return `${mins}m ${secs}s`;
}

function relativeFromNow(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) {
    return `${Math.max(1, Math.floor(diff / 1000))}s ago`;
  }
  if (diff < 3_600_000) {
    return `${Math.floor(diff / 60_000)}m ago`;
  }
  if (diff < 86_400_000) {
    return `${Math.floor(diff / 3_600_000)}h ago`;
  }

  return `${Math.floor(diff / 86_400_000)}d ago`;
}

function findLastFailedLog(logs: JobLogModel[]): JobLogModel | null {
  for (let i = logs.length - 1; i >= 0; i--) {
    if (logs[i].eventType === 'Failed') {
      return logs[i];
    }
  }

  return null;
}

interface ParsedException {
  type: string | null;
  message: string;
  thrownAt: string | null;
  frames: StackFrame[];
}

interface StackFrame {
  text: string;
  isUser: boolean;
  isThrowSite: boolean;
}

function parseException(raw: string | null): ParsedException | null {
  if (!raw) {
    return null;
  }
  const lines = raw.split(/\r?\n/);
  let exceptionType: string | null = null;
  let message = '';
  const frames: StackFrame[] = [];
  const first = lines[0]?.trim();
  if (first) {
    const m = first.match(/^([A-Za-z0-9_.]+Exception(?:`\d+)?)(?::\s*(.*))?$/);
    if (m) {
      exceptionType = m[1];
      message = (m[2] ?? '').trim();
    } else {
      message = first;
    }
  }

  let thrownAt: string | null = null;
  for (let i = 1; i < lines.length; i++) {
    const l = lines[i].trim();
    if (!l) {
      continue;
    }
    if (l.startsWith('at ')) {
      const isFramework = /^at (System\.|Microsoft\.|Warp\.|MailKit\.|Npgsql\.|EntityFrameworkCore\.)/.test(l);
      const isUser = !isFramework;
      const isThrowSite = isUser && thrownAt === null;
      if (isThrowSite) {
        const m2 = l.match(/^at ([^(\s]+)/);
        thrownAt = m2 ? m2[1] : null;
      }
      frames.push({ text: l, isUser, isThrowSite });
    }
  }

  return { type: exceptionType, message: message || 'Job failed.', thrownAt, frames };
}

function shortenJobId(id: string): { short: string; tail: string } {
  if (id.length <= 8) {
    return { short: id, tail: '' };
  }

  return { short: id.slice(0, 8), tail: id.slice(8) };
}

interface JobDetailBoldProps {
  job: UnifiedJobDetailModel;
  handlerLogs: JobLogModel[];
}

export function JobDetailBold({ job, handlerLogs }: JobDetailBoldProps) {
  const requeue = useRequeueJob();
  const deleteJob = useDeleteJob();

  const failedLog = findLastFailedLog(job.logs);
  const parsed = parseException(failedLog?.exception ?? null);
  const heroMessage = parsed?.message ?? failedLog?.message ?? 'Job failed.';
  const exceptionType = parsed?.type ?? null;
  const thrownAt = parsed?.thrownAt ?? null;

  const finalizedAt = failedLog?.timestamp ?? null;
  const elapsedMs = finalizedAt ? new Date(finalizedAt).getTime() - new Date(job.createTime).getTime() : null;
  const stoppedAgo = relativeFromNow(finalizedAt);
  const attemptCount = job.retriedTimes + 1;
  const maxAttempts = job.maxRetries + 1;

  const lastAttemptDuration = formatDuration(failedLog?.durationMs ?? null);
  const lastWorker = failedLog?.workerId ?? null;

  const idParts = shortenJobId(job.id);

  return (
    <div className="space-y-3.5">
      {/* HERO */}
      <div className="relative overflow-hidden rounded-[14px] border border-border bg-panel shadow-[inset_0_1px_0_rgba(255,255,255,0.03),0_1px_2px_rgba(0,0,0,0.25)]">
        {/* red wash */}
        <div
          className="pointer-events-none absolute inset-0"
          style={{ background: 'radial-gradient(50% 100% at 0% 0%, rgba(220,38,38,0.18), transparent 60%)' }}
        />
        {/* diagonal scanlines */}
        <div
          className="pointer-events-none absolute inset-0 opacity-[0.14]"
          style={{
            backgroundImage:
              'repeating-linear-gradient(135deg, transparent 0 9px, rgba(220,38,38,0.16) 9px 10px)',
          }}
        />
        {/* left red bar */}
        <div
          className="pointer-events-none absolute left-0 top-0 bottom-0 w-[3px]"
          style={{ background: 'linear-gradient(180deg, var(--warp-red), rgba(220,38,38,0.55) 60%, transparent)' }}
        />

        <div className="relative flex flex-col gap-6 px-6 py-6 lg:flex-row lg:items-start">
          <div className="min-w-0 flex-1">
            <div className="mb-3.5 flex flex-wrap items-center gap-2.5">
              <span
                className="mono inline-flex items-center gap-1.5 rounded px-2 py-1 text-[10px] font-bold uppercase tracking-[0.14em] text-white"
                style={{ background: 'var(--warp-red)' }}
              >
                <span className="h-1 w-1 rounded-full bg-white" />
                Failed permanently
              </span>
              <span className="mono text-[10.5px] font-semibold uppercase tracking-[0.1em] text-text-dim">
                {attemptCount} / {maxAttempts} attempts
                {stoppedAgo ? ` · stopped ${stoppedAgo}` : ''}
                {elapsedMs != null ? ` · ${formatDuration(elapsedMs)} elapsed` : ''}
              </span>
            </div>

            <div className="font-display text-[36px] font-semibold leading-[1.04] tracking-[-0.03em] text-foreground lg:text-[46px]">
              {heroMessage}
            </div>

            {(exceptionType || thrownAt) && (
              <div className="mono mt-2 text-[12.5px] font-medium text-warp-red">
                {exceptionType ?? 'Exception'}
                {thrownAt && (
                  <>
                    {' · at '}
                    <span className="underline underline-offset-[3px]">{thrownAt}</span>()
                  </>
                )}
              </div>
            )}

            <div className="mt-5 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-text-dim">
              <span className="inline-flex items-center gap-1.5">
                <span className="mono text-[10.5px] uppercase tracking-[0.06em] text-text-mute">Job</span>
                <span className="mono text-[13px] font-semibold text-foreground">{idParts.short}</span>
                {idParts.tail && <span className="mono text-[11px] text-text-mute">{idParts.tail}…</span>}
              </span>
              <span className="hidden h-3.5 w-px bg-border sm:block" />
              <span className="inline-flex items-center gap-1.5">
                <span className="mono text-[10.5px] uppercase tracking-[0.06em] text-text-mute">Type</span>
                <span className="text-[13px] font-medium text-foreground">{shortType(job.type)}</span>
              </span>
              {job.queue && (
                <>
                  <span className="hidden h-3.5 w-px bg-border sm:block" />
                  <span className="inline-flex items-center gap-1.5">
                    <span className="mono text-[10.5px] uppercase tracking-[0.06em] text-text-mute">Queue</span>
                    <span className="mono rounded border border-border bg-panel-2 px-1.5 py-0.5 text-[12px] font-medium text-foreground">
                      {job.queue}
                    </span>
                  </span>
                </>
              )}
              {job.traceId && (
                <>
                  <span className="hidden h-3.5 w-px bg-border sm:block" />
                  <span className="inline-flex items-center gap-1.5">
                    <span className="mono text-[10.5px] uppercase tracking-[0.06em] text-text-mute">Trace</span>
                    <Link
                      to={`/trace/${job.traceId}`}
                      className="mono text-[12px] font-medium text-warp-blue underline underline-offset-[3px]"
                    >
                      {job.traceId.slice(0, 12)} ↗
                    </Link>
                  </span>
                </>
              )}
            </div>
          </div>

          {/* action stack */}
          <div className="flex w-full flex-col gap-2 lg:w-[220px] lg:flex-shrink-0">
            <button
              onClick={() => requeue.mutate(job.id)}
              disabled={requeue.isPending}
              className="inline-flex items-center justify-center gap-2 rounded-[9px] border px-3.5 py-2.5 text-[13.5px] font-semibold text-white shadow-[0_0_24px_rgba(34,197,94,0.20),inset_0_1px_0_rgba(255,255,255,0.20)] transition hover:brightness-110 disabled:opacity-60"
              style={{
                borderColor: 'var(--warp-green)',
                background: 'linear-gradient(180deg, var(--warp-green), #15803d)',
              }}
            >
              <Repeat size={15} /> Requeue job
            </button>
            <div className="flex gap-1.5">
              {job.traceId ? (
                <Link
                  to={`/trace/${job.traceId}`}
                  className="inline-flex flex-1 items-center justify-center gap-1.5 rounded-lg border border-border bg-panel px-2.5 py-2 text-[12px] font-medium text-foreground hover:bg-panel-2"
                >
                  <ExternalLink size={12} /> View trace
                </Link>
              ) : (
                <button
                  disabled
                  className="flex-1 cursor-not-allowed rounded-lg border border-border bg-panel px-2.5 py-2 text-[12px] font-medium text-text-mute"
                >
                  No trace
                </button>
              )}
              <button
                onClick={() => deleteJob.mutate(job.id)}
                disabled={deleteJob.isPending}
                className="inline-flex flex-1 items-center justify-center gap-1.5 rounded-lg border bg-transparent px-2.5 py-2 text-[12px] font-medium text-warp-red transition hover:bg-warp-red-soft disabled:opacity-60"
                style={{ borderColor: 'rgba(220,38,38,0.35)' }}
              >
                <Trash2 size={12} /> Delete
              </button>
            </div>
            <div className="mt-1.5 rounded-lg border border-border bg-panel-2 px-2.5 py-2">
              <Eyebrow>Last attempt</Eyebrow>
              <div className="mt-1 flex items-center justify-between text-xs">
                <span className="text-text-dim">duration</span>
                <span className="mono font-semibold text-foreground">{lastAttemptDuration ?? '—'}</span>
              </div>
              <div className="mt-0.5 flex items-center justify-between text-xs">
                <span className="text-text-dim">worker</span>
                <span className="mono font-semibold text-foreground">{lastWorker ?? '—'}</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* BODY GRID */}
      <div className="grid grid-cols-1 gap-3.5 lg:grid-cols-[1.6fr_1fr]">
        <div className="flex flex-col gap-3.5">
          {handlerLogs.length > 0 && <JobLogs jobId={job.id} logs={handlerLogs} />}
          <StackTraceCard
            exception={parsed}
            rawException={failedLog?.exception ?? null}
            thrownAtIso={failedLog?.timestamp ?? null}
            workerId={lastWorker}
          />
          {(job.message || (job.metadata && Object.keys(job.metadata).length > 0)) && (
            <PayloadCard payload={job.message} metadata={job.metadata} />
          )}
        </div>
        <div className="flex flex-col gap-3.5">
          <ExecutionContextCard job={job} workerId={lastWorker} />
          <IdentityCard job={job} />
        </div>
      </div>
    </div>
  );
}

// ----- StackTraceCard -----
function StackTraceCard({
  exception,
  rawException,
  thrownAtIso,
  workerId,
}: {
  exception: ParsedException | null;
  rawException: string | null;
  thrownAtIso: string | null;
  workerId: string | null;
}) {
  const frames = exception?.frames ?? [];
  const hasFrames = frames.length > 0;

  return (
    <Panel accent="var(--warp-red)">
      <PanelHeader eyebrow={`Stack trace · ${frames.length} frames`} eyebrowColor="var(--warp-red)" />
      <div className="mono bg-[color:var(--panel-2)] text-[11.5px] leading-[1.65]">
        {exception && (
          <div className="border-b border-border bg-warp-red-soft px-4 py-3">
            <div className="font-semibold text-warp-red">
              {exception.type ?? 'Exception'}: {exception.message}
            </div>
            <div className="mt-1 text-[10.5px] text-text-dim">
              {workerId ? `thrown in ${workerId}` : 'thrown'} {thrownAtIso ? `at ${new Date(thrownAtIso).toISOString()}` : ''}
            </div>
          </div>
        )}
        {hasFrames ? (
          <>
            {frames.map((f, n) => {
              const lineNo = n + 1;

              return (
                <div
                  key={n}
                  className="grid items-baseline py-1"
                  style={{
                    gridTemplateColumns: '34px 28px 1fr',
                    background: f.isThrowSite ? 'var(--warp-red-soft)' : 'transparent',
                    borderLeft: `2px solid ${f.isThrowSite ? 'var(--warp-red)' : 'transparent'}`,
                    color: f.isUser ? 'var(--foreground)' : 'var(--text-dim)',
                    opacity: f.isUser ? 1 : 0.75,
                  }}
                >
                  <span className="pr-2 text-right text-[10.5px] text-text-mute">{lineNo}</span>
                  <span>
                    {f.isThrowSite && <span className="font-bold text-warp-red">▶ </span>}
                    {f.isUser && !f.isThrowSite && <span className="text-warp-green">● </span>}
                    {!f.isUser && <span className="text-text-mute">○ </span>}
                  </span>
                  <span className="pr-3.5 break-all">{f.text.replace(/^at /, '')}</span>
                </div>
              );
            })}
            <div className="flex items-center justify-between border-t border-border px-4 py-2 text-[10.5px] text-text-mute">
              <span>
                <span className="text-warp-green">●</span> your code &nbsp;
                <span className="text-warp-red">▶</span> origin &nbsp;
                <span className="text-text-mute">○</span> framework
              </span>
              <span>{frames.filter(f => !f.isUser).length} framework frames</span>
            </div>
          </>
        ) : rawException ? (
          <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap p-4 text-[11.5px] text-text-dim">{rawException}</pre>
        ) : (
          <div className="px-4 py-6 text-center text-[11.5px] text-text-mute">No stack trace recorded.</div>
        )}
      </div>
    </Panel>
  );
}

// ----- PayloadCard -----
function tokenizeJsonLine(line: string): React.ReactNode {
  const m = line.match(/^(\s*)("[^"]+")(:\s*)(.+?)(,?\s*)$/);
  if (!m) {
    return <span>{line}</span>;
  }
  const [, ind, key, sep, val, tail] = m;
  let valEl: React.ReactNode;
  const trimmed = val.trim();
  if (val.startsWith('"')) {
    valEl = <span className="text-warp-amber">{val}</span>;
  } else if (/^(true|false|null)$/.test(trimmed)) {
    valEl = <span style={{ color: 'var(--warp-purple)' }}>{val}</span>;
  } else if (/^-?\d/.test(trimmed)) {
    valEl = <span className="text-warp-blue">{val}</span>;
  } else {
    valEl = <span>{val}</span>;
  }

  return (
    <span>
      {ind}
      <span className="text-warp-green">{key}</span>
      {sep}
      {valEl}
      {tail}
    </span>
  );
}

function PayloadCard({ payload, metadata }: { payload: string | null; metadata: Record<string, string> | null }) {
  let pretty = '';
  let byteCount = 0;
  if (payload) {
    try {
      pretty = JSON.stringify(JSON.parse(payload), null, 2);
    } catch {
      pretty = payload;
    }
    byteCount = new Blob([payload]).size;
  }
  const hasMeta = metadata && Object.keys(metadata).length > 0;

  return (
    <Panel>
      <PanelHeader
        eyebrow="Payload · the input that failed"
        action={byteCount > 0 ? <span className="mono text-[10.5px] text-text-mute">{byteCount} bytes</span> : undefined}
      />
      {pretty && (
        <pre className="mono m-0 max-h-[60vh] overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.7] text-text-dim">
          {pretty.split('\n').map((l, i) => (
            <div key={i}>{tokenizeJsonLine(l)}</div>
          ))}
        </pre>
      )}
      {hasMeta && (
        <div className="border-t border-border px-4 py-3">
          <Eyebrow>Metadata</Eyebrow>
          <pre className="mono mt-2 max-h-40 overflow-auto text-[11.5px] text-text-dim">
            {JSON.stringify(metadata, null, 2)}
          </pre>
        </div>
      )}
    </Panel>
  );
}

// ----- StatRow -----
function StatRow({
  k,
  v,
  mono,
  accent,
}: {
  k: string;
  v: React.ReactNode;
  mono?: boolean;
  accent?: string;
}) {
  return (
    <div className="flex items-center justify-between border-b border-dashed border-border py-2 last:border-b-0">
      <span className="mono text-[11px] uppercase tracking-[0.06em] text-text-mute">{k}</span>
      <span
        className={`${mono ? 'mono ' : ''}text-right text-[12.5px] font-medium`}
        style={{ color: accent ?? 'var(--foreground)' }}
      >
        {v}
      </span>
    </div>
  );
}

function ExecutionContextCard({ job, workerId }: { job: UnifiedJobDetailModel; workerId: string | null }) {
  const idempotency = job.metadata?.['IdempotencyKey'] ?? null;
  const concurrencyKey = job.metadata?.['ConcurrencyKey'] ?? null;

  return (
    <Panel accent="var(--warp-blue)">
      <PanelHeader eyebrow="Execution context" eyebrowColor="var(--warp-blue)" />
      <div className="px-4 py-2">
        {workerId && <StatRow k="Worker" v={workerId} mono accent="var(--warp-purple)" />}
        {job.queue && <StatRow k="Queue" v={job.queue} mono />}
        {concurrencyKey && <StatRow k="Mutex" v={concurrencyKey} mono />}
        {idempotency && <StatRow k="Idempotency" v={<span className="text-warp-green">{idempotency}</span>} mono />}
        <StatRow k="Attempts" v={`${job.retriedTimes + 1} / ${job.maxRetries + 1}`} mono />
      </div>
    </Panel>
  );
}

function IdentityCard({ job }: { job: UnifiedJobDetailModel }) {
  return (
    <Panel>
      <PanelHeader eyebrow="Identity" />
      <div className="px-4 py-2">
        <StatRow k="ID" v={shortId(job.id)} mono />
        {job.type && <StatRow k="Type" v={shortType(job.type)} />}
        {job.handlerType && <StatRow k="Handler" v={shortType(job.handlerType)} />}
        <StatRow k="Created" v={new Date(job.createTime).toLocaleString()} mono />
        {job.scheduleTime && <StatRow k="Scheduled" v={new Date(job.scheduleTime).toLocaleString()} mono />}
        {job.traceId && (
          <StatRow
            k="Trace"
            v={
              <Link to={`/trace/${job.traceId}`} className="text-warp-blue underline underline-offset-[3px]">
                {job.traceId.slice(0, 12)} ↗
              </Link>
            }
            mono
          />
        )}
      </div>
    </Panel>
  );
}
