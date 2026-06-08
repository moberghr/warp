import { Repeat, Trash2, ExternalLink, ChevronDown, Copy } from 'lucide-react';
import { Link } from 'react-router-dom';
import { useState } from 'react';
import { PageHeader, type PageHeaderMetaItem } from '@/components/v2/PageHeader';
import { Panel, PanelHeader, Eyebrow } from '@/components/v2/Panel';
import { shortId, shortType, stateName, formatDateTime } from '@/utils/format';
import { formatDuration, relativeFromNow } from '@/utils/jobDetailFormatters';
import { useDeleteJob, useRequeueJob } from '@/api/hooks/useJobs';
import { useConfirm } from '@/components/forms/useConfirm';
import type { UnifiedJobDetailModel, JobLogModel } from '@/types';
import { JobLogs } from './JobLogs';

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

interface JobDetailBoldProps {
  job: UnifiedJobDetailModel;
  handlerLogs: JobLogModel[];
}

export function JobDetailBold({ job, handlerLogs }: JobDetailBoldProps) {
  const requeue = useRequeueJob();
  const deleteJob = useDeleteJob();
  const { confirm, dialog: confirmDialog } = useConfirm();

  const askDelete = async () => {
    const ok = await confirm({
      title: 'Delete job?',
      description: `Delete ${shortId(job.id)}? This cannot be undone.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (ok) {
      deleteJob.mutate(job.id);
    }
  };

  const failedLog = findLastFailedLog(job.logs);
  const parsed = parseException(failedLog?.exception ?? null);
  const heroMessage = parsed?.message ?? failedLog?.message ?? 'Job failed.';
  const exceptionType = parsed?.type ?? null;

  const finalizedAt = failedLog?.timestamp ?? null;
  const stoppedAgo = relativeFromNow(finalizedAt);
  const attemptCount = job.retriedTimes + 1;
  const maxAttempts = job.maxRetries + 1;
  const exhausted = attemptCount >= maxAttempts;

  const lastAttemptDuration = formatDuration(failedLog?.durationMs ?? null);
  const lastWorker = failedLog?.workerId ?? null;
  const createdAgo = relativeFromNow(job.createTime);

  const meta: PageHeaderMetaItem[] = [];
  if (job.type) {
    meta.push({ k: 'Type', v: shortType(job.type) });
  }
  if (job.queue) {
    meta.push({ k: 'Queue', v: job.queue });
  }
  meta.push({
    k: 'Attempts',
    v: (
      <span style={{ color: 'var(--state-failed)' }}>
        {attemptCount}
        <span className="rel">
          / {maxAttempts} max{exhausted ? ' — exhausted' : ''}
        </span>
      </span>
    ),
  });
  meta.push({
    k: 'Failed at',
    v: finalizedAt ? formatDateTime(finalizedAt) : '—',
    rel: stoppedAgo ?? undefined,
  });
  meta.push({ k: 'ID', v: job.id, copy: job.id });

  return (
    <div className="flex flex-col gap-[18px]">
      <PageHeader
        kindLabel="Job"
        title={shortId(job.id)}
        pill={<span className="warp-pill failed">{stateName(job.currentState)}</span>}
        actions={
          <>
            <button
              type="button"
              onClick={() => requeue.mutate(job.id)}
              disabled={requeue.isPending}
              className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-foreground hover:bg-panel-2 disabled:opacity-60"
            >
              <Repeat size={13} /> Requeue
            </button>
            {job.traceId && (
              <Link
                to={`/trace/${job.traceId}`}
                className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-foreground hover:bg-panel-2"
              >
                <ExternalLink size={13} /> Trace
              </Link>
            )}
            <button
              type="button"
              onClick={askDelete}
              disabled={deleteJob.isPending}
              className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-2.5 py-1.5 text-[12.5px] font-medium text-warp-red hover:bg-warp-red-soft disabled:opacity-60"
            >
              <Trash2 size={13} /> Delete
            </button>
          </>
        }
        meta={meta}
      />

      {/* Last-error card + payload (info row) */}
      <div className="warp-info-row">
        <ErrorCard
          exception={parsed}
          message={heroMessage}
          exceptionType={exceptionType}
          attemptCount={attemptCount}
          maxAttempts={maxAttempts}
          finalizedAt={finalizedAt}
          workerId={lastWorker}
          duration={lastAttemptDuration}
          createdAgo={createdAgo}
        />
        {(job.message || (job.metadata && Object.keys(job.metadata).length > 0)) && (
          <PayloadCard payload={job.message} metadata={job.metadata} />
        )}
      </div>

      {/* Logs + stack frames + context cards */}
      <div className="grid grid-cols-1 gap-[18px] lg:grid-cols-[1.6fr_1fr]">
        <div className="flex flex-col gap-[18px]">
          {handlerLogs.length > 0 && <JobLogs jobId={job.id} logs={handlerLogs} />}
          {parsed?.frames && parsed.frames.length > 0 && <StackTraceCard frames={parsed.frames} />}
        </div>
        <div className="flex flex-col gap-[18px]">
          <ExecutionContextCard job={job} workerId={lastWorker} />
          <IdentityCard job={job} />
        </div>
      </div>
      {confirmDialog}
    </div>
  );
}

// ----- ErrorCard -----
function ErrorCard({
  exception,
  message,
  exceptionType,
  attemptCount,
  maxAttempts,
  finalizedAt,
  workerId,
  duration,
  createdAgo: _createdAgo,
}: {
  exception: ParsedException | null;
  message: string;
  exceptionType: string | null;
  attemptCount: number;
  maxAttempts: number;
  finalizedAt: string | null;
  workerId: string | null;
  duration: string | null;
  createdAgo: string | null;
}) {
  const [expanded, setExpanded] = useState(false);
  const frames = exception?.frames ?? [];
  const userFrames = frames.filter(f => f.isUser);
  const visibleFrames = expanded ? frames : userFrames.length > 0 ? userFrames.slice(0, 5) : frames.slice(0, 5);
  const hiddenCount = frames.length - visibleFrames.length;

  return (
    <div
      className="warp-inner-card relative"
      style={{
        borderLeft: '3px solid var(--state-failed)',
        background: 'linear-gradient(180deg, var(--state-failed-bg) 0%, var(--card) 60%)',
      }}
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="warp-card-label" style={{ color: 'var(--state-failed)' }}>
          Last error
        </span>
        <span className="warp-pill failed dot">
          Failed · attempt {attemptCount}/{maxAttempts}
        </span>
      </div>

      <div className="mt-3">
        {exceptionType && (
          <div
            className="font-semibold"
            style={{
              color: 'var(--state-failed)',
              fontFamily: 'var(--font-mono)',
              fontSize: 13.5,
              letterSpacing: '-0.005em',
            }}
          >
            {exceptionType}
          </div>
        )}
        <div className="mt-1 text-[13px] leading-[1.5] text-text-dim">{message}</div>
      </div>

      {frames.length > 0 && (
        <pre
          className="mono mt-3 overflow-x-auto rounded-lg px-3.5 py-3 text-[11.5px] leading-[1.55] whitespace-pre"
          style={{ background: '#1f1c18', color: '#e6e1d3' }}
        >
          {visibleFrames.map((f, i) => (
            <div key={i}>
              {f.isThrowSite && <span style={{ color: '#f4c87f' }}>▶ </span>}
              {f.text}
            </div>
          ))}
        </pre>
      )}

      <div className="mt-3 flex items-center justify-between gap-2">
        {hiddenCount > 0 ? (
          <button
            type="button"
            className="mono inline-flex items-center gap-1.5 rounded px-2 py-1 text-[11.5px] text-text-dim hover:bg-accent"
            onClick={() => setExpanded(e => !e)}
          >
            <ChevronDown size={11} style={{ transform: expanded ? 'rotate(180deg)' : undefined }} />
            {expanded ? 'Hide framework frames' : `Show full backtrace (${frames.length} frames)`}
          </button>
        ) : (
          <span />
        )}
        <span className="mono text-[11px] text-text-mute">
          {finalizedAt ? formatDateTime(finalizedAt) : ''}
          {duration ? ` · ${duration}` : ''}
          {workerId ? ` · ${workerId}` : ''}
        </span>
      </div>
    </div>
  );
}

// ----- StackTraceCard (full frames, body grid) -----
function StackTraceCard({ frames }: { frames: StackFrame[] }) {
  return (
    <Panel accent="var(--state-failed)">
      <PanelHeader eyebrow={`Stack trace · ${frames.length} frames`} eyebrowColor="var(--state-failed)" />
      <div className="mono bg-[color:var(--panel-2)] text-[11.5px] leading-[1.65]">
        {frames.map((f, n) => (
          <div
            key={n}
            className="grid items-baseline py-1"
            style={{
              gridTemplateColumns: '34px 28px 1fr',
              background: f.isThrowSite ? 'var(--state-failed-bg)' : 'transparent',
              borderLeft: `2px solid ${f.isThrowSite ? 'var(--state-failed)' : 'transparent'}`,
              color: f.isUser ? 'var(--foreground)' : 'var(--text-dim)',
              opacity: f.isUser ? 1 : 0.75,
            }}
          >
            <span className="pr-2 text-right text-[10.5px] text-text-mute">{n + 1}</span>
            <span>
              {f.isThrowSite && <span className="font-bold text-warp-red">▶ </span>}
              {f.isUser && !f.isThrowSite && <span className="text-warp-green">● </span>}
              {!f.isUser && <span className="text-text-mute">○ </span>}
            </span>
            <span className="pr-3.5 break-all">{f.text.replace(/^at /, '')}</span>
          </div>
        ))}
        <div className="flex items-center justify-between border-t border-border px-4 py-2 text-[10.5px] text-text-mute">
          <span>
            <span className="text-warp-green">●</span> your code &nbsp;
            <span className="text-warp-red">▶</span> origin &nbsp;
            <span className="text-text-mute">○</span> framework
          </span>
          <span>{frames.filter(f => !f.isUser).length} framework frames</span>
        </div>
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
    valEl = <span style={{ color: '#bfe0a8' }}>{val}</span>;
  } else if (/^(true|false|null)$/.test(trimmed)) {
    valEl = <span style={{ color: '#d99a64' }}>{val}</span>;
  } else if (/^-?\d/.test(trimmed)) {
    valEl = <span style={{ color: '#e8b97a' }}>{val}</span>;
  } else {
    valEl = <span>{val}</span>;
  }

  return (
    <span>
      {ind}
      <span style={{ color: '#9bb6e0' }}>{key}</span>
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
    <div className="warp-table-wrap">
      <div className="warp-table-toolbar">
        <span className="grouped">application/json · {byteCount} bytes</span>
        <button
          type="button"
          className="inline-flex items-center gap-1.5 rounded px-2 py-1 text-[11.5px] text-text-dim hover:bg-accent"
          onClick={() => payload && navigator.clipboard?.writeText(payload)}
        >
          <Copy size={11} /> Copy
        </button>
      </div>
      {pretty && (
        <pre
          className="mono m-0 overflow-x-auto whitespace-pre px-4 py-3 text-[12px] leading-[1.6]"
          style={{ background: '#1f1c18', color: '#e6e1d3', maxHeight: 360 }}
        >
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
    </div>
  );
}

// ----- StatRow / context cards -----
function StatRow({ k, v, mono, accent }: { k: string; v: React.ReactNode; mono?: boolean; accent?: string }) {
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
        <StatRow k="Created" v={formatDateTime(job.createTime)} mono />
        {job.scheduleTime && <StatRow k="Scheduled" v={formatDateTime(job.scheduleTime)} mono />}
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
