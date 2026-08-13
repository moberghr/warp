import { useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import { toast } from 'sonner';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import * as api from '@/api';
import { WebhookDeliveryStatus, WebhookSigning } from '@/types/webhooks';
import { OutcomeBadge, formatMs, HttpStatus } from '@/pages/adapters/shared';

export default function WebhookDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [redelivering, setRedelivering] = useState(false);

  const detailQuery = useQuery({
    queryKey: ['webhooks', 'detail', id] as const,
    queryFn: () => api.getWebhookDetail(id!),
    enabled: !!id,
  });

  const notFound =
    detailQuery.isError &&
    axios.isAxiosError(detailQuery.error) &&
    detailQuery.error.response?.status === 404;

  const detail = detailQuery.data;

  const redeliver = async () => {
    if (!id) {
      return;
    }
    setRedelivering(true);
    try {
      const outcome = await api.redeliverWebhook(id);
      switch (outcome) {
        case 'enqueued':
          toast.success('Redelivery enqueued');
          await detailQuery.refetch();
          break;
        case 'not-found':
          // The row aged past its retention window between load and click — refetch to surface the 404 view.
          toast.error('This delivery no longer exists');
          await detailQuery.refetch();
          break;
        case 'in-flight':
          // 409 Rejected: another attempt is already live, so redelivery is a no-op — refetch to show it.
          toast.error('Delivery is in flight — it already has a live attempt');
          await detailQuery.refetch();
          break;
        case 'unavailable':
          // 409 Unavailable: this process has no webhooks worker; the delivery was left untouched.
          toast.error("Redelivery isn't available from this process — run it from a server host");
          break;
        default:
          toast.error('Unable to redeliver');
          break;
      }
    } finally {
      setRedelivering(false);
    }
  };

  if (notFound) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/webhooks" className="text-sm text-muted-foreground hover:underline">← Webhooks</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            No webhook delivery matches this id — it may have expired past its retention window.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (detailQuery.isError) return <ErrorState message="Unable to load webhook delivery" />;
  if (detailQuery.isLoading || !detail) return <LoadingState />;

  const canRedeliver =
    detail.status === WebhookDeliveryStatus.Delivered ||
    detail.status === WebhookDeliveryStatus.Exhausted;

  return (
    <div>
      <div className="flex items-start justify-between mb-4 gap-3">
        <div>
          <Link to="/webhooks" className="text-sm text-muted-foreground hover:underline">← Webhooks</Link>
          <div className="flex items-center gap-3 mt-1">
            <h1 className="text-2xl font-bold">{detail.eventType}</h1>
            <StatusPill status={detail.status} />
          </div>
          <div className="mt-1 font-mono text-xs text-muted-foreground">{detail.eventId}</div>
        </div>
        {canRedeliver && (
          <Button size="sm" onClick={redeliver} disabled={redelivering}>
            {redelivering ? 'Redelivering…' : 'Redeliver'}
          </Button>
        )}
      </div>

      {/* Self-contained contract — everything the executor needs, secret never revealed. */}
      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Delivery contract</CardTitle></CardHeader>
        <CardContent className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-1 text-sm">
          <Field label="URL"><span className="font-mono text-xs break-all">{detail.url}</span></Field>
          <Field label="Group">
            {detail.groupName ? <span className="font-mono text-xs break-all">{detail.groupName}</span> : <Dash />}
          </Field>
          <Field label="Reference">
            {detail.reference ? <span className="font-mono text-xs">{detail.reference}</span> : <Dash />}
          </Field>
          <Field label="Signing">{signingLabel(detail.signingMode)}</Field>
          <Field label="Secret">
            {detail.hasSecret ? <span className="font-mono text-xs">***</span> : <span className="text-muted-foreground">None</span>}
          </Field>
          <Field label="Success codes">
            <span className="font-mono text-xs">{detail.successCodesJson ?? 'Any 2xx'}</span>
          </Field>
          <Field label="Retry schedule">
            <span className="font-mono text-xs">{formatSchedule(detail.retryScheduleSeconds)}</span>
          </Field>
          <Field label="Attempts">{detail.attemptCount}</Field>
          <Field label="Next attempt">
            {detail.nextAttemptAt ? <RelativeTime date={detail.nextAttemptAt} /> : <Dash />}
          </Field>
          <Field label="Created"><RelativeTime date={detail.createdAt} /></Field>
          <Field label="Expires">
            {detail.expireAt ? <RelativeTime date={detail.expireAt} /> : <span className="text-muted-foreground">Never</span>}
          </Field>
        </CardContent>
      </Card>

      {/* Redacted headers — Authorization-class values arrive already reduced to ***. */}
      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Headers</CardTitle></CardHeader>
        <CardContent>
          {detail.headersJson ? (
            <pre className="text-xs font-mono bg-muted/50 rounded-md p-3 overflow-auto max-h-60">{prettyJson(detail.headersJson)}</pre>
          ) : (
            <div className="text-sm text-muted-foreground">No per-delivery headers.</div>
          )}
        </CardContent>
      </Card>

      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Payload</CardTitle></CardHeader>
        <CardContent>
          <pre className="text-xs font-mono bg-muted/50 rounded-md p-3 overflow-auto max-h-96">{prettyJson(detail.payloadJson)}</pre>
        </CardContent>
      </Card>

      {/* Attempt timeline — projected from the delivery's AdapterCallLog rows (CorrelationId = id). */}
      <Card>
        <CardHeader><CardTitle className="text-base">Attempts ({detail.attempts.length})</CardTitle></CardHeader>
        <CardContent>
          {detail.attempts.length === 0 ? (
            <div className="text-center text-muted-foreground py-4 text-sm">No attempts recorded yet.</div>
          ) : (
            <div className="space-y-2">
              {detail.attempts.map((attempt, index) => (
                <div key={attempt.callId} className="border-l-2 border-muted pl-3 py-1">
                  <div className="flex flex-wrap items-baseline gap-2 text-sm">
                    <span className="text-muted-foreground tabular-nums">#{index + 1}</span>
                    <RelativeTime date={attempt.timestamp} />
                    <OutcomeBadge outcome={attempt.outcome} />
                    <HttpStatus code={attempt.statusCode} className="tabular-nums text-muted-foreground" />
                    <span className="tabular-nums text-muted-foreground">{formatMs(attempt.durationMs)}</span>
                  </div>
                  {attempt.exceptionType && (
                    <div className="mt-0.5 font-mono text-xs text-destructive">{attempt.exceptionType}</div>
                  )}
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-2 py-0.5">
      <span className="text-muted-foreground min-w-28">{label}</span>
      <span className="break-all">{children}</span>
    </div>
  );
}

function Dash() {
  return <span className="text-muted-foreground/40">—</span>;
}

function signingLabel(mode: WebhookSigning): string {
  switch (mode) {
    case WebhookSigning.StandardWebhooks:
      return 'Standard Webhooks';
    case WebhookSigning.Custom:
      return 'Custom';
    default:
      return 'None';
  }
}

// Human-readable retry cadence, e.g. [60, 600, 3600, 21600] → "1m, 10m, 1h, 6h". An empty
// schedule means a single attempt with no retries.
function formatSchedule(seconds: number[]): string {
  if (seconds.length === 0) {
    return 'Single attempt (no retries)';
  }

  return seconds.map(formatSeconds).join(', ');
}

function formatSeconds(total: number): string {
  if (total < 60) {
    return `${Math.round(total)}s`;
  }
  if (total < 3600) {
    return `${Math.round(total / 60)}m`;
  }
  if (total < 86400) {
    return `${Math.round(total / 3600)}h`;
  }

  return `${Math.round(total / 86400)}d`;
}

function prettyJson(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

const statusStyles: Record<WebhookDeliveryStatus, { label: string; cls: string }> = {
  [WebhookDeliveryStatus.Pending]: {
    label: 'Pending',
    cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400',
  },
  [WebhookDeliveryStatus.Delivered]: {
    label: 'Delivered',
    cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400',
  },
  [WebhookDeliveryStatus.Exhausted]: {
    label: 'Exhausted',
    cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400',
  },
};

function StatusPill({ status }: { status: WebhookDeliveryStatus }) {
  const style = statusStyles[status] ?? {
    label: 'Unknown',
    cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400',
  };

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${style.cls}`}>
      {style.label}
    </span>
  );
}
