import { WebhookDeliveryStatus, WebhookEndpointState, WebhookFailureReason } from '@/types/webhooks';

// Display helpers shared by the webhook pages (8.1). Pure functions — the components live in WebhookBadges.tsx.

export const deliveryStatusStyles: Record<WebhookDeliveryStatus, { label: string; cls: string }> = {
  [WebhookDeliveryStatus.Pending]: { label: 'Pending', cls: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400' },
  [WebhookDeliveryStatus.Delivered]: { label: 'Delivered', cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' },
  [WebhookDeliveryStatus.Exhausted]: { label: 'Exhausted', cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' },
  [WebhookDeliveryStatus.Parked]: { label: 'Parked', cls: 'bg-violet-100 text-violet-800 dark:bg-violet-900/30 dark:text-violet-300' },
  [WebhookDeliveryStatus.Cancelled]: { label: 'Cancelled', cls: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-400' },
};

export const unknownStatusStyle = { label: 'Unknown', cls: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400' };

const failureReasonLabels: Record<WebhookFailureReason, string> = {
  [WebhookFailureReason.Timeout]: 'Timeout',
  [WebhookFailureReason.DnsFailure]: 'DNS failure',
  [WebhookFailureReason.ConnectionRefused]: 'Connection refused',
  [WebhookFailureReason.TlsError]: 'TLS error',
  [WebhookFailureReason.ConnectionFailed]: 'Connection failed',
  [WebhookFailureReason.AddressNotPublic]: 'Address not public',
  [WebhookFailureReason.UnsuccessfulStatus]: 'Unsuccessful status',
  [WebhookFailureReason.Other]: 'Other',
};

/**
 * The last attempt's result in a few words: "HTTP 503" for an unsuccessful status (the code says more than the
 * reason), the reason label for a transport failure, "HTTP 200" for a success, or null before the first attempt.
 */
export function lastResultLabel(lastStatusCode: number | null, lastFailureReason: WebhookFailureReason | null): string | null {
  if (lastFailureReason === WebhookFailureReason.UnsuccessfulStatus && lastStatusCode != null) {
    return `HTTP ${lastStatusCode}`;
  }
  if (lastFailureReason != null) {
    return failureReasonLabels[lastFailureReason] ?? 'Other';
  }

  return lastStatusCode != null ? `HTTP ${lastStatusCode}` : null;
}

export const endpointStateStyles: Record<WebhookEndpointState, { label: string; cls: string }> = {
  [WebhookEndpointState.Healthy]: { label: 'Healthy', cls: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' },
  [WebhookEndpointState.Failing]: { label: 'Failing', cls: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' },
};

/** A send-to-delivered duration: ms below a second, then seconds, minutes, hours. */
export function formatDeliveryDuration(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) {
    return '—';
  }
  if (ms < 1000) {
    return `${Math.round(ms)} ms`;
  }
  if (ms < 60_000) {
    return `${(ms / 1000).toFixed(1)} s`;
  }
  if (ms < 3_600_000) {
    return `${(ms / 60_000).toFixed(1)} min`;
  }

  return `${(ms / 3_600_000).toFixed(1)} h`;
}

export function formatSuccessRate(rate: number | null | undefined): string {
  return rate == null || !Number.isFinite(rate) ? '—' : `${(rate * 100).toFixed(1)}%`;
}

/** Deliveries a "Cancel all" would cancel (pending + parked). */
export function cancellableCount(group: { pending: number; parked: number }): number {
  return group.pending + group.parked;
}
