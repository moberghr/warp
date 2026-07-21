import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent } from '@/components/ui/card';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { OutcomeBadge, formatMs, Pane, Field, PayloadPane } from './shared';

// Full, linkable detail page for a single outbound adapter call (formerly a drawer). The URL
// carries both the adapter name and the call id, so a call is shareable and survives refresh.
export default function AdapterCallDetailPage() {
  const { name: rawName, callId: rawCallId } = useParams<{ name: string; callId: string }>();
  const name = rawName ? decodeURIComponent(rawName) : '';
  const callId = rawCallId ? decodeURIComponent(rawCallId) : '';

  const query = useQuery({
    queryKey: ['adapters', 'call', name, callId] as const,
    queryFn: () => api.getAdapterCall(name, callId),
    enabled: !!name && !!callId,
  });

  const call = query.data;
  const backTo = `/adapters/${encodeURIComponent(name)}`;

  return (
    <div>
      <div className="mb-4">
        <Link to={backTo} className="text-sm text-muted-foreground hover:underline">← {name}</Link>
        <h1 className="text-2xl font-bold mt-1">Call detail</h1>
      </div>

      {query.isError && <ErrorState message="Unable to load call detail" />}
      {query.isLoading && <LoadingState />}

      {call && (
        <div className="space-y-4">
          <Card>
            <CardContent className="p-4 space-y-4">
              <div className="flex items-center gap-2">
                <span className="font-mono text-sm">{call.operation}</span>
                <OutcomeBadge outcome={call.outcome} />
              </div>

              <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                <Field label="Timestamp"><RelativeTime date={call.timestamp} /></Field>
                <Field label="Duration">{formatMs(call.durationMs)}</Field>
                <Field label="Attempts">{call.attempts}</Field>
                <Field label="Status">{call.statusCode ?? '—'}</Field>
                {call.groupName && <Field label="Group"><span className="font-mono text-xs">{call.groupName}</span></Field>}
                <Field label="Machine"><span className="font-mono text-xs">{call.machineName}</span></Field>
                {call.traceId && <Field label="Trace"><span className="font-mono text-xs">{call.traceId}</span></Field>}
                {call.correlationId && <Field label="Correlation"><span className="font-mono text-xs">{call.correlationId}</span></Field>}
              </div>
            </CardContent>
          </Card>

          {call.exceptionType && (
            <Pane title="Exception">
              <div className="font-mono text-xs text-destructive">{call.exceptionType}</div>
              {call.exceptionMessage && (
                <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">{call.exceptionMessage}</pre>
              )}
            </Pane>
          )}

          <PayloadPane title="Request" summary={call.requestSummary} headers={call.requestHeaders} body={call.requestBody} />
          <PayloadPane title="Response" headers={call.responseHeaders} body={call.responseBody} />
        </div>
      )}
    </div>
  );
}
