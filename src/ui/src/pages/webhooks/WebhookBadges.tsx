import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import type { WebhookDeliveryStatus, WebhookEndpointState, WebhookFailureReason, WebhookGroupModel } from '@/types/webhooks';
import { WebhookEndpointState as EndpointState } from '@/types/webhooks';
import { cancellableCount, deliveryStatusStyles, endpointStateStyles, lastResultLabel, unknownStatusStyle } from './webhookDisplay';

const pillClass = 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium';

/** Delivery status pill: Pending amber, Delivered green, Exhausted red, Parked violet, Cancelled gray. */
export function WebhookStatusPill({ status }: { status: WebhookDeliveryStatus }) {
  const style = deliveryStatusStyles[status] ?? unknownStatusStyle;

  return <span className={`${pillClass} ${style.cls}`}>{style.label}</span>;
}

/** Endpoint health badge; "—" for a group without a health record (untracked, or never failed). */
export function EndpointHealthBadge({ health }: { health: WebhookEndpointState | null | undefined }) {
  if (health == null) {
    return <span className="text-muted-foreground/40">—</span>;
  }

  const style = endpointStateStyles[health];

  return <span className={`${pillClass} ${style.cls}`}>{style.label}</span>;
}

/** The last attempt's status code or failure reason, with when it happened. */
export function LastResult({
  lastStatusCode,
  lastFailureReason,
  lastAttemptAt,
}: {
  lastStatusCode: number | null;
  lastFailureReason: WebhookFailureReason | null;
  lastAttemptAt: string | null;
}) {
  const label = lastResultLabel(lastStatusCode, lastFailureReason);
  if (!label) {
    return <span className="text-muted-foreground/40">—</span>;
  }

  return (
    <span className="text-xs">
      <span className={lastFailureReason != null ? 'text-destructive' : 'text-green-700 dark:text-green-400'}>{label}</span>
      {lastAttemptAt && (
        <span className="text-muted-foreground"> · <RelativeTime date={lastAttemptAt} /></span>
      )}
    </span>
  );
}

/**
 * "Retry now" (failing endpoints only) and "Cancel all" (when something is pending or parked) for one endpoint group.
 * Clicks never bubble to a clickable row. Both invalidate every webhooks query on success.
 */
export function EndpointActions({ group, size = 'sm' }: { group: WebhookGroupModel; size?: 'sm' | 'xs' }) {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const cancellable = cancellableCount(group);
  const failing = group.health === EndpointState.Failing;

  if (!failing && cancellable === 0) {
    return null;
  }

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['webhooks'] });

  const retry = async () => {
    setBusy(true);
    try {
      const outcome = await api.retryWebhookEndpoint(group.key);
      switch (outcome) {
        case 'probing':
          toast.success('Probe sent — the oldest parked delivery is being re-sent');
          break;
        case 'healthy':
          toast.error('The endpoint is healthy — nothing to retry');
          break;
        case 'nothing-parked':
          toast.error('Nothing is parked — the endpoint recovers on its next successful delivery');
          break;
        case 'not-found':
          toast.error('No health is recorded for this endpoint');
          break;
        case 'unavailable':
          toast.error("Retry isn't available from this process — run it from a server host");
          break;
        default:
          toast.error('Unable to retry the endpoint');
          break;
      }
      await refresh();
    } finally {
      setBusy(false);
    }
  };

  const cancelAll = async () => {
    setConfirming(false);
    setBusy(true);
    try {
      const cancelled = await api.cancelWebhookGroup(group.key);
      toast.success(`Cancelled ${cancelled} ${cancelled === 1 ? 'delivery' : 'deliveries'}`);
      await refresh();
    } catch {
      toast.error('Unable to cancel the deliveries');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex items-center justify-end gap-2" onClick={(e) => e.stopPropagation()}>
      {failing && (
        <Button size={size} variant="outline" disabled={busy} onClick={retry}>
          Retry now
        </Button>
      )}
      {cancellable > 0 && (
        <Button size={size} variant="destructive" disabled={busy} onClick={() => setConfirming(true)}>
          Cancel all
        </Button>
      )}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Cancel ${cancellable} ${cancellable === 1 ? 'delivery' : 'deliveries'}?`}
        description={`Every pending and parked delivery to ${group.key} is cancelled and will not be sent unless redelivered. An attempt already in flight still reaches the receiver.`}
        confirmLabel="Cancel deliveries"
        cancelLabel="Keep them"
        variant="destructive"
        onConfirm={cancelAll}
      />
    </div>
  );
}
