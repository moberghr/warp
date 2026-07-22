import { describe, it, expect } from 'vitest';
import { addonChips } from './addonChips';

describe('addonChips', () => {
  it('returns no chips for empty metadata', () => {
    expect(addonChips({})).toEqual([]);
  });

  it('renders retry as tried/max', () => {
    expect(addonChips({ MaxRetries: 3, RetriedTimes: 1 })).toEqual(['Retry 1/3']);
  });

  it('renders RetriedTimes=0 rather than dropping the zero', () => {
    // The numeric coercion must preserve a legitimate 0 (a review-flagged edge).
    expect(addonChips({ MaxRetries: 3, RetriedTimes: 0 })).toEqual(['Retry 0/3']);
  });

  it('defaults RetriedTimes to 0 when absent', () => {
    expect(addonChips({ MaxRetries: 5 })).toEqual(['Retry 0/5']);
  });

  it('coerces string-typed metadata values (as deserialized from JSON)', () => {
    expect(addonChips({ MaxRetries: '2', RetriedTimes: '1' })).toEqual(['Retry 1/2']);
  });

  it('renders a rate-limit chip only when key, count and window are all present', () => {
    expect(addonChips({ RateLimitKey: 'sendgrid', RateLimitCount: 240, RateLimitWindowSeconds: 60 }))
      .toEqual(['Rate limit 240/60s · sendgrid']);
    expect(addonChips({ RateLimitKey: 'sendgrid', RateLimitCount: 240 })).toEqual([]);
  });

  it('renders Mutex for limit 1 and Semaphore for limit > 1', () => {
    expect(addonChips({ ConcurrencyKey: 'k' })).toEqual(['Mutex k']);
    expect(addonChips({ ConcurrencyKey: 'k', ConcurrencyLimit: 1 })).toEqual(['Mutex k']);
    expect(addonChips({ ConcurrencyKey: 'k', ConcurrencyLimit: 5 })).toEqual(['Semaphore k (5)']);
  });

  it('renders a timeout chip', () => {
    expect(addonChips({ TimeoutSeconds: 30 })).toEqual(['Timeout 30s']);
  });

  it('combines multiple addons in order', () => {
    expect(addonChips({
      MaxRetries: 3,
      RetriedTimes: 1,
      RateLimitKey: 'api', RateLimitCount: 10, RateLimitWindowSeconds: 1,
      ConcurrencyKey: 'c', ConcurrencyLimit: 2,
      TimeoutSeconds: 15,
    })).toEqual(['Retry 1/3', 'Rate limit 10/1s · api', 'Semaphore c (2)', 'Timeout 15s']);
  });
});
