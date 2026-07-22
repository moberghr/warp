// #241: turn known addon metadata (retry / rate-limit / concurrency / timeout) into friendly chips.
// Pure — extracted from DetailPage so it can be unit-tested without pulling the component tree.
// The raw metadata stays available via the expandable Metadata block on the detail page.
export function addonChips(metadata: Record<string, unknown>): string[] {
  const chips: string[] = [];
  const num = (k: string): number | undefined => {
    const v = metadata[k];
    if (v == null || v === '') return undefined;
    const n = Number(v);
    return Number.isNaN(n) ? undefined : n;
  };
  const str = (k: string): string | undefined => (metadata[k] == null ? undefined : String(metadata[k]));

  const maxRetries = num('MaxRetries');
  if (maxRetries !== undefined) chips.push(`Retry ${num('RetriedTimes') ?? 0}/${maxRetries}`);

  const rlKey = str('RateLimitKey');
  const rlCount = num('RateLimitCount');
  const rlWindow = num('RateLimitWindowSeconds');
  if (rlKey && rlCount !== undefined && rlWindow !== undefined) chips.push(`Rate limit ${rlCount}/${rlWindow}s · ${rlKey}`);

  const ck = str('ConcurrencyKey');
  if (ck) {
    const limit = num('ConcurrencyLimit') ?? 1;
    chips.push(limit > 1 ? `Semaphore ${ck} (${limit})` : `Mutex ${ck}`);
  }

  const timeout = num('TimeoutSeconds');
  if (timeout !== undefined) chips.push(`Timeout ${timeout}s`);

  return chips;
}
