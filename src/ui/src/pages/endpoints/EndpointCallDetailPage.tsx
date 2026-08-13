import { useMemo } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent } from '@/components/ui/card';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { StateBadge } from '@/components/StateBadge';
import type { State } from '@/types';
import type { EndpointRelatedJob } from '@/types/endpoints';
import * as api from '@/api';
import { OutcomeBadge, formatMs, HttpStatus, Pane, Field, PayloadPane, parseTags } from '../adapters/shared';

// Full, linkable detail page for a single inbound endpoint call (formerly a drawer). The URL
// carries both the endpoint id and the call id, so a call is shareable and survives refresh.
export default function EndpointCallDetailPage() {
  const { id: rawId, callId: rawCallId } = useParams<{ id: string; callId: string }>();
  const id = rawId ? decodeURIComponent(rawId) : '';
  const callId = rawCallId ? decodeURIComponent(rawCallId) : '';

  const query = useQuery({
    queryKey: ['endpoints', 'call', id, callId] as const,
    queryFn: () => api.getEndpointCall(id, callId),
    enabled: !!id && !!callId,
  });

  const call = query.data;
  const backTo = `/endpoints/${encodeURIComponent(id)}`;

  return (
    <div>
      <div className="mb-4">
        <Link to={backTo} className="text-sm text-muted-foreground hover:underline">← Endpoint</Link>
        <h1 className="text-2xl font-bold mt-1">Call detail</h1>
      </div>

      {query.isError && <ErrorState message="Unable to load call detail" />}
      {query.isLoading && <LoadingState />}

      {call && (
        <div className="space-y-4">
          <Card>
            <CardContent className="p-4 space-y-4">
              <div className="flex items-center gap-2">
                <span className="font-mono text-sm">
                  <span className="mr-1 font-semibold uppercase text-muted-foreground">{call.method}</span>
                  {call.routeTemplate}
                </span>
                <OutcomeBadge outcome={call.outcome} />
              </div>

              <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                <Field label="Timestamp"><RelativeTime date={call.timestamp} /></Field>
                <Field label="Duration">{formatMs(call.durationMs)}</Field>
                <Field label="Status"><HttpStatus code={call.statusCode} /></Field>
                {call.groupName && <Field label="Caller"><span className="font-mono text-xs">{call.groupName}</span></Field>}
                <Field label="Remote IP"><span className="font-mono text-xs">{call.remoteIp ?? '—'}</span></Field>
                <Field label="User"><span className="font-mono text-xs">{call.user ?? '—'}</span></Field>
                <Field label="Machine"><span className="font-mono text-xs">{call.machineName}</span></Field>
                {call.traceId && <Field label="Trace"><span className="font-mono text-xs">{call.traceId}</span></Field>}
              </div>
            </CardContent>
          </Card>

          {call.userAgent && (
            <Pane title="User agent">
              <div className="font-mono text-xs break-words">{call.userAgent}</div>
            </Pane>
          )}

          {call.exceptionType && (
            <Pane title="Exception">
              <div className="font-mono text-xs text-destructive">{call.exceptionType}</div>
              {call.exceptionMessage && (
                <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">{call.exceptionMessage}</pre>
              )}
            </Pane>
          )}

          <PayloadPane title="Request" headers={call.requestHeaders} body={call.requestBody} />
          <PayloadPane title="Response" headers={call.responseHeaders} body={call.responseBody} />

          <TagsSection tagsJson={call.tagsJson} />

          <RelatedJobsSection jobs={call.relatedJobs} traceId={call.traceId} />
        </div>
      )}
    </div>
  );
}

// Custom enrichment tags — render defensively; a null/empty or malformed payload skips the section.
function TagsSection({ tagsJson }: { tagsJson: string | null }) {
  const tags = useMemo(() => parseTags(tagsJson), [tagsJson]);
  if (tags.length === 0) {
    return null;
  }

  return (
    <Pane title="Tags">
      <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
        {tags.map(([key, value]) => (
          <div key={key} className="flex gap-2">
            <span className="text-muted-foreground min-w-24 font-mono text-xs">{key}</span>
            <span className="font-mono text-xs break-words">{value}</span>
          </div>
        ))}
      </div>
    </Pane>
  );
}

// Jobs enqueued during this request (same trace id) — the request→jobs drill-down. Skipped when
// no jobs were spawned; a "View full trace" link shows when the request carried a trace id.
function RelatedJobsSection({ jobs, traceId }: { jobs: EndpointRelatedJob[]; traceId: string | null }) {
  if (jobs.length === 0) {
    return null;
  }

  return (
    <Pane title="Related jobs">
      <div className="space-y-1.5">
        {jobs.map((job) => (
          <div key={job.id} className="flex items-center gap-2 text-sm">
            <Link to={`/detail/${job.id}`} className="font-mono text-xs text-primary hover:underline truncate max-w-56">
              {job.type ?? job.id}
            </Link>
            <StateBadge state={job.state as State} />
            <span className="font-mono text-xs text-muted-foreground">{job.queue}</span>
          </div>
        ))}
      </div>
      {traceId && (
        <div className="mt-2">
          <Link to={`/trace/${traceId}`} className="text-xs text-primary hover:underline">
            View full trace →
          </Link>
        </div>
      )}
    </Pane>
  );
}
