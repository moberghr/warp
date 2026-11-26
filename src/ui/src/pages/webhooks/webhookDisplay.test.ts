import { describe, expect, it } from 'vitest';
import { WebhookFailureReason } from '@/types/webhooks';
import { cancellableCount, formatDeliveryDuration, formatSuccessRate, lastResultLabel } from './webhookDisplay';

describe('lastResultLabel', () => {
  it.each([
    [503, WebhookFailureReason.UnsuccessfulStatus, 'HTTP 503'],
    [null, WebhookFailureReason.Timeout, 'Timeout'],
    [null, WebhookFailureReason.DnsFailure, 'DNS failure'],
    [null, WebhookFailureReason.ConnectionRefused, 'Connection refused'],
    [null, WebhookFailureReason.TlsError, 'TLS error'],
    [null, WebhookFailureReason.ConnectionFailed, 'Connection failed'],
    [null, WebhookFailureReason.AddressNotPublic, 'Address not public'],
    [null, WebhookFailureReason.Other, 'Other'],
    [200, null, 'HTTP 200'],
    [null, null, null],
  ] as const)('status %s, reason %s → %s', (status, reason, expected) => {
    expect(lastResultLabel(status, reason)).toBe(expected);
  });
});

describe('formatDeliveryDuration', () => {
  it.each([
    [null, '—'],
    [420, '420 ms'],
    [2500, '2.5 s'],
    [90_000, '1.5 min'],
    [5_400_000, '1.5 h'],
  ] as const)('%s → %s', (ms, expected) => {
    expect(formatDeliveryDuration(ms)).toBe(expected);
  });
});

describe('endpoint figures', () => {
  it('formats the success rate and counts what Cancel all would cancel', () => {
    expect(formatSuccessRate(0.9876)).toBe('98.8%');
    expect(formatSuccessRate(null)).toBe('—');
    expect(cancellableCount({ pending: 2, parked: 3 })).toBe(5);
  });
});
