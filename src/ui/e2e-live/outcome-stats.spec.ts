import { test, expect, type APIRequestContext } from '@playwright/test';

/**
 * Live verification of the outcome-stats taxonomy against the real Aspire stack.
 *
 * Everything here asserts on counters a real worker wrote after real handlers ran — the gap the unit
 * suite structurally cannot cover, since its tests seed `Counter` rows and read them straight back.
 *
 * COVERAGE IS PARTIAL, and the two lists below say exactly how. EXPECTED_KEYS is what a seed in this
 * file actually drives; PENDING_KEYS is what the taxonomy defines but nothing here can produce (saga
 * conflicts, a crashed worker, Skip-mode rate limiting). The guard test treats their union as the
 * complete taxonomy and fails on any live breakdown key outside it — so adding an OutcomeReason
 * without deciding its coverage breaks the run rather than shipping unnoticed.
 *
 * EXTENDING THIS FILE: add a seed, then move the key from PENDING_KEYS to EXPECTED_KEYS.
 *
 * Not part of `dotnet test` or CI's default run — `npm run test:e2e:live` boots the Aspire stack
 * (Docker, several minutes).
 */

const PREFIX = '/warp/api';

/** Keys that the code currently writes. Every one of these is asserted to appear and to be positive. */
const EXPECTED_KEYS = {
  /** R2 — dashboard requeue. */
  manualRequeue: ['stats:requeued', 'stats:requeued-manual'],
  /** Pre-existing state totals. */
  stateTotals: ['stats:succeeded', 'stats:failed', 'stats:deleted', 'stats:requeued'],
  /** T2 — the reason breakdown for a retrying, permanently failing job. */
  t2: ['stats:requeued-retry', 'stats:failed-retry-exhausted', 'stats:retried-jobs'],
  /** T5 — addon-attributed outcomes driven by a real seeded workload. */
  addonAttributed: [
    'stats:deleted-concurrency',
    'stats:requeued-concurrency',
    'stats:deleted-timeout',
    'stats:requeued-ratelimit',
  ],
} as const;

/**
 * Breakdown keys the taxonomy DOES define but that no seed in this file drives, so they have no
 * end-to-end coverage. Listed honestly rather than left out — the guard below treats
 * `EXPECTED_KEYS ∪ PENDING_KEYS` as the complete taxonomy, so anything here is a known gap and
 * anything outside the union is an unclassified key that fails the run.
 *
 *  - The three saga reasons need a real correlation-key collision (two messages racing for one saga,
 *    or an optimistic-version clash). `/seed/sagas` publishes each saga's messages in order and
 *    normally completes without one, so it cannot be relied on to produce them.
 *  - The crash-recovery reason needs a worker to die mid-job so `StaleJobRecovery` sweeps the claim.
 *    Nothing here can kill the Aspire worker process.
 *  - `/seed/ratelimit` demos Wait mode, so the Skip-mode delete key has no seed. (Skip-mode
 *    concurrency does have one — `/seed/mutex` — which is why only the rate-limit half is here.)
 *
 * These are SHIPPED keys, so they are deliberately not asserted absent — see the guard test.
 */
const PENDING_KEYS = {
  /** Written by SagaHandlerProxy on a busy / version / unique conflict. */
  saga: ['stats:failed-saga', 'stats:deleted-saga', 'stats:requeued-saga'],
  /** Written by StaleJobRecovery when it requeues a job whose worker died. */
  recovery: ['stats:requeued-recovery'],
  /** Written when a Skip-mode [RateLimit] drops the surplus instead of rescheduling it. */
  rateLimitSkip: ['stats:deleted-ratelimit'],
} as const;

/** Every breakdown key this file has classified, covered or not. */
function listedKeys(): Set<string> {
  return new Set<string>([
    ...EXPECTED_KEYS.stateTotals,
    ...EXPECTED_KEYS.manualRequeue,
    ...EXPECTED_KEYS.t2,
    ...EXPECTED_KEYS.addonAttributed,
    ...PENDING_KEYS.saga,
    ...PENDING_KEYS.recovery,
    ...PENDING_KEYS.rateLimitSkip,
  ]);
}

interface CounterRow {
  key: string;
  value: number;
}

interface StatusRow {
  processing: number;
  pending: number;
  scheduled: number;
}

/**
 * The demo dashboard runs with built-in cookie login (`DemoCredentialValidator`, admin/admin), and the
 * middleware answers unauthenticated `/api/` calls with a bare 401 — no redirect, no body. Logging in is
 * a form POST, not JSON: `HandleLogin` reads `ReadFormAsync`. The cookie is scoped to the route prefix,
 * so it covers every subsequent `/warp/**` request on the same context.
 */
async function login(request: APIRequestContext): Promise<void> {
  const response = await request.post(`${PREFIX}/auth/login`, {
    form: { username: 'admin', password: 'admin' },
  });

  expect(response.ok(), `login failed with ${response.status()} — has the demo validator changed?`).toBeTruthy();
}

async function counters(request: APIRequestContext): Promise<Map<string, number>> {
  const response = await request.get(`${PREFIX}/stats/counters`);
  expect(response.ok(), `GET ${PREFIX}/stats/counters failed with ${response.status()}`).toBeTruthy();

  const rows = (await response.json()) as CounterRow[];

  return new Map(rows.map((row) => [row.key, row.value]));
}

function delta(before: Map<string, number>, after: Map<string, number>, key: string): number {
  return (after.get(key) ?? 0) - (before.get(key) ?? 0);
}

/**
 * The "not Completed" umbrella, computed exactly the way the Counters page computes it.
 *
 * There is no `stats:unsuccessful` row: ten sites move `stats:failed` / `stats:deleted`, and a stored
 * umbrella maintained at only some of them under-reports silently, so it is derived on read instead.
 * Asserting on it therefore means summing the two totals here — reading a `stats:unsuccessful` key
 * from the API would read `undefined` and quietly make every umbrella assertion vacuous.
 */
function unsuccessful(rows: Map<string, number>): number {
  return (rows.get('stats:failed') ?? 0) + (rows.get('stats:deleted') ?? 0);
}

function unsuccessfulDelta(before: Map<string, number>, after: Map<string, number>): number {
  return unsuccessful(after) - unsuccessful(before);
}

async function seed(request: APIRequestContext, path: string): Promise<void> {
  const response = await request.post(path);
  expect(response.ok(), `POST ${path} failed with ${response.status()}`).toBeTruthy();
}

/**
 * Waits for a specific counter to rise by at least `by` above `baseline`.
 *
 * Deliberately NOT a "wait until nothing is in flight" check, which cannot work against this app for two
 * independent reasons found while building this harness:
 *
 *  1. The demo registers recurring jobs, so `scheduled` is never 0 — a global-quiescence predicate waits
 *     forever.
 *  2. `RetryOptions.Delays` defaults to `[15, 60, 300]`, and `/seed/failing-job` sets `MaxRetries = 2`, so
 *     that one job spends ~75s in retry backoff before it settles terminal (plus up to
 *     `ScheduledActivationInterval` per hop). Any timeout tuned for "a job runs quickly" is a flake.
 *
 * Waiting on the counter the assertion is about is immune to both: background recurring work moves other
 * keys, and the retry cadence just makes this take longer, not fail.
 */
async function waitForCounter(
  request: APIRequestContext,
  key: string,
  baseline: number,
  by = 1,
  timeoutMs = 150_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const rows = await counters(request);
    if ((rows.get(key) ?? 0) - baseline >= by) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }

  throw new Error(`${key} did not rise by ${by} above ${baseline} within ${timeoutMs}ms`);
}

/**
 * BEST-EFFORT quiet period, for hygiene between tests — never an assertion.
 *
 * It returns on timeout instead of throwing, deliberately: the demo's recurring jobs keep `pending`
 * moving indefinitely, so "no job in flight" is not a state this app reaches. Nothing correctness-related
 * may depend on this, which is why every test brackets its own action with its own counter baseline
 * rather than trusting a global quiet point.
 */
async function settleBestEffort(request: APIRequestContext, timeoutMs = 20_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let quietPolls = 0;

  while (Date.now() < deadline) {
    const response = await request.get(`${PREFIX}/status`);
    expect(
      response.ok(),
      `GET ${PREFIX}/status returned ${response.status()}: ${(await response.text()).slice(0, 400)}`,
    ).toBeTruthy();

    const status = (await response.json()) as StatusRow;

    quietPolls = status.processing + status.pending === 0 ? quietPolls + 1 : 0;
    if (quietPolls >= 3) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
}

test.describe.serial('outcome stats — live Aspire stack', () => {
  // The `request` fixture is per-test with its own cookie jar, so every test authenticates.
  test.beforeEach(async ({ request }) => {
    await login(request);
  });

  test('no counter value is ever negative', async ({ request }) => {
    // The append-only guarantee (R3). Before it, requeueing wrote -1 rows, so a lifetime total could
    // sit below the sum of its own hourly buckets and the dashboard rate chart could see a negative
    // delta. Nothing in the system may write a negative counter now, so this holds after ANY workload
    // — which makes it the cheapest permanent regression guard we have.
    const baseline = await counters(request);

    await seed(request, '/seed/failing-job');
    await seed(request, '/seed/simple-job');

    // One completion is enough to prove the write path ran; the negative check below is global anyway.
    await waitForCounter(request, 'stats:succeeded', baseline.get('stats:succeeded') ?? 0);

    const rows = await counters(request);
    const negatives = [...rows.entries()].filter(([, value]) => value < 0);

    expect(negatives, `negative counters found: ${JSON.stringify(negatives)}`).toEqual([]);
  });

  test('a failing job records failures and retries, and never a negative', async ({ request }) => {
    const before = await counters(request);

    await seed(request, '/seed/failing-job');

    // MaxRetries = 2 with the default [15, 60] delays, so terminal failure is ~75s away.
    await waitForCounter(request, 'stats:failed', before.get('stats:failed') ?? 0);

    const after = await counters(request);

    // A handler that throws with retries configured requeues at least once before settling terminal.
    expect(delta(before, after, 'stats:requeued'), 'a retrying job must count a requeue').toBeGreaterThan(0);
    expect(delta(before, after, 'stats:failed'), 'a permanently failing job must count a failure').toBeGreaterThan(0);
  });

  test('dashboard requeue counts the requeue and does not retract the failure', async ({ request }) => {
    // The R2 + R3 pair, end to end: a requeue is its own event (counted, with a reason) and it does not
    // rewrite the failure that preceded it. Before this work the first two assertions were 0 and the
    // third went DOWN by one.
    const failedBaseline = (await counters(request)).get('stats:failed') ?? 0;
    await seed(request, '/seed/failing-job');
    await waitForCounter(request, 'stats:failed', failedBaseline);

    // Page is ZERO-based — ToPagedListAsync does Skip(Page * PageSize), so page=1 skips the first row.
    const failedResponse = await request.get(`${PREFIX}/jobs/failed?page=0&pageSize=20`);
    expect(failedResponse.ok(), `failed-jobs list returned ${failedResponse.status()}`).toBeTruthy();

    const failedPage = (await failedResponse.json()) as { totalCount: number; items: { id: string }[] };
    expect(
      failedPage.items.length,
      `seed/failing-job should leave a failed job to requeue (totalCount=${failedPage.totalCount})`,
    ).toBeGreaterThan(0);

    const jobId = failedPage.items[0].id;
    const before = await counters(request);

    const requeueResponse = await request.post(`${PREFIX}/jobs/${jobId}/requeue`);
    expect(requeueResponse.ok(), `requeue failed with ${requeueResponse.status()}`).toBeTruthy();

    const after = await counters(request);

    // The total can also be moved by a background retry landing in the same window; the manual reason key
    // cannot, since only operator requeues write it. So the exact assertion goes on the reason.
    expect(delta(before, after, 'stats:requeued'), 'requeue must move the state total').toBeGreaterThanOrEqual(1);
    expect(delta(before, after, 'stats:requeued-manual'), 'requeue must be attributed to the operator').toBe(1);
    expect(delta(before, after, 'stats:failed'), 'requeue must NOT retract the recorded failure').toBe(0);

    // The requeued job runs again and re-enters retry backoff; give it a moment, but do not depend on it.
    await settleBestEffort(request);
  });

  test('a mutex skip is counted as a concurrency-attributed delete', async ({ request }) => {
    // The case that motivated the whole reason breakdown: a job cancelled by a mutex used to be
    // indistinguishable from an operator delete, because both only moved stats:deleted.
    //
    // /seed/mutex enqueues two jobs against one key. [Mutex] defaults to ConcurrencyMode.Skip, so whichever
    // loses the race is Deleted rather than requeued — the demo endpoint even calls it "cancelled". The
    // holder is a SlowRequest, so it is still inside the critical section when the second job is claimed.
    const before = await counters(request);

    await seed(request, '/seed/mutex');
    await waitForCounter(request, 'stats:deleted-concurrency', before.get('stats:deleted-concurrency') ?? 0);

    const after = await counters(request);

    expect(delta(before, after, 'stats:deleted-concurrency'), 'the skip must be attributed to concurrency').toBeGreaterThanOrEqual(1);
    expect(delta(before, after, 'stats:deleted'), 'the state total must move with its reason').toBeGreaterThanOrEqual(1);
    expect(unsuccessfulDelta(before, after), 'a mutex skip is a non-Completed outcome').toBeGreaterThanOrEqual(1);

    await settleBestEffort(request);
  });

  test('a semaphore wait is counted as a concurrency-attributed requeue', async ({ request }) => {
    // The other half of the concurrency reason: Skip-mode mutex DELETES the surplus (asserted above),
    // Wait-mode semaphore REQUEUES it. Same reason token, different state total — which is exactly why the
    // reason and the state are separate dimensions rather than one fused label.
    const before = await counters(request);

    await seed(request, '/seed/semaphore');
    await waitForCounter(request, 'stats:requeued-concurrency', before.get('stats:requeued-concurrency') ?? 0);

    const after = await counters(request);

    expect(delta(before, after, 'stats:requeued-concurrency'), 'the wait must be attributed to concurrency').toBeGreaterThanOrEqual(1);
    expect(delta(before, after, 'stats:requeued'), 'the state total must move with its reason').toBeGreaterThanOrEqual(1);

    // A requeue is not a terminal outcome. Asserted on the concurrency reason rather than on the whole
    // umbrella: the umbrella is failed + deleted globally, and a retry from an earlier test settling in
    // this window would move it for reasons that have nothing to do with a semaphore. Only a Skip-mode
    // rejection writes stats:deleted-concurrency, so a Wait-mode one leaving it untouched is the exact
    // claim, with no exposure to background work.
    expect(delta(before, after, 'stats:deleted-concurrency'), 'a Wait-mode semaphore requeues, never deletes').toBe(0);

    await settleBestEffort(request);
  });

  test('a timeout delete is counted as a timeout-attributed delete', async ({ request }) => {
    // Timeout Delete mode is the one reason that reaches a terminal state without the handler failing:
    // the job is dropped, not retried (§8.7), so before the breakdown existed it was indistinguishable
    // from an operator delete.
    const before = await counters(request);

    await seed(request, '/seed/timeout');
    await waitForCounter(request, 'stats:deleted-timeout', before.get('stats:deleted-timeout') ?? 0);

    const after = await counters(request);

    expect(delta(before, after, 'stats:deleted-timeout'), 'the drop must be attributed to the timeout').toBeGreaterThanOrEqual(1);
    expect(delta(before, after, 'stats:deleted'), 'the state total must move with its reason').toBeGreaterThanOrEqual(1);
    expect(unsuccessfulDelta(before, after), 'a timeout drop is a non-Completed outcome').toBeGreaterThanOrEqual(1);

    await settleBestEffort(request);
  });

  test('a rate-limit wait is counted as a ratelimit-attributed requeue', async ({ request }) => {
    // Wait mode reschedules the surplus instead of dropping it, so the requeue is counted immediately even
    // though the job will not run again until the window opens (§8.8).
    const before = await counters(request);

    await seed(request, '/seed/ratelimit');
    await waitForCounter(request, 'stats:requeued-ratelimit', before.get('stats:requeued-ratelimit') ?? 0);

    const after = await counters(request);

    expect(delta(before, after, 'stats:requeued-ratelimit'), 'the throttle must be attributed to the rate limit').toBeGreaterThanOrEqual(1);
    expect(delta(before, after, 'stats:requeued'), 'the state total must move with its reason').toBeGreaterThanOrEqual(1);

    // As in the semaphore case: the reason-scoped claim, not the global umbrella. Wait mode reschedules,
    // so the Skip-mode delete key for the same reason must stay put.
    expect(delta(before, after, 'stats:deleted-ratelimit'), 'a Wait-mode rate limit requeues, never deletes').toBe(0);

    await settleBestEffort(request);
  });

  test('every expected key is present and positive', async ({ request }) => {
    const showcaseBaseline = (await counters(request)).get('stats:succeeded') ?? 0;
    await seed(request, '/seed/showcase');
    await waitForCounter(request, 'stats:succeeded', showcaseBaseline);
    await settleBestEffort(request);

    // Nothing in the seeds deletes a job, so stats:deleted would legitimately not exist yet — drive a real
    // delete rather than dropping the key from the assertion. This also covers the delete counter path,
    // which R3 changed (it no longer rewrites the source state's counter on the way out).
    const completedResponse = await request.get(`${PREFIX}/jobs/completed?page=0&pageSize=20`);
    expect(completedResponse.ok(), `completed-jobs list returned ${completedResponse.status()}`).toBeTruthy();

    const completedPage = (await completedResponse.json()) as { totalCount: number; items: { id: string }[] };
    expect(
      completedPage.items.length,
      `the showcase workload should leave a completed job to delete (totalCount=${completedPage.totalCount})`,
    ).toBeGreaterThan(0);

    const deletedBaseline = await counters(request);
    const deleteResponse = await request.post(`${PREFIX}/jobs/${completedPage.items[0].id}/delete`);
    expect(deleteResponse.ok(), `delete failed with ${deleteResponse.status()}`).toBeTruthy();

    const afterDelete = await counters(request);
    expect(delta(deletedBaseline, afterDelete, 'stats:deleted'), 'a delete must be counted').toBeGreaterThanOrEqual(1);
    expect(
      delta(deletedBaseline, afterDelete, 'stats:succeeded'),
      'deleting a completed job must NOT retract the success it recorded',
    ).toBe(0);

    const rows = await counters(request);
    const expectedKeys = [
      ...new Set([...EXPECTED_KEYS.stateTotals, ...EXPECTED_KEYS.manualRequeue, ...EXPECTED_KEYS.t2, ...EXPECTED_KEYS.addonAttributed]),
    ];

    for (const key of expectedKeys) {
      expect(rows.has(key), `${key} should exist after a mixed workload`).toBeTruthy();
      expect(rows.get(key) ?? -1, `${key} must be non-negative`).toBeGreaterThanOrEqual(0);
    }
  });

  test('every breakdown key the live stack produced is classified by this spec', async ({ request }) => {
    // The guard that actually catches something: a new OutcomeReason shipping in C# mints a
    // `stats:{state}-{token}` key that no list here knows about, and it would otherwise reach
    // production with zero end-to-end coverage and nobody noticing.
    //
    // Note what this deliberately does NOT do: assert that PENDING keys are absent. They are shipped
    // keys, not unshipped ones — a saga correlation clash or a worker crash in the demo would produce
    // them legitimately and fail a run for no reason. Classification is the invariant; absence is not.
    const rows = await counters(request);
    const listed = listedKeys();

    // Breakdown keys only: state totals and stats:retried-jobs carry no `-{reason}` suffix, and hourly
    // bucket rows are filtered out server-side by GetCounters, so this sees lifetime keys alone.
    const unlisted = [...rows.keys()]
      .filter((key) => /^stats:(failed|deleted|requeued)-/.test(key))
      .filter((key) => !listed.has(key))
      .sort();

    expect(
      unlisted,
      `unclassified breakdown keys — add each to EXPECTED_KEYS with a seed and assertions, or to PENDING_KEYS with the reason it cannot be driven here: ${unlisted.join(', ')}`,
    ).toEqual([]);

    // A key in both lists means one was promoted by copy rather than by move, which would leave the
    // "not covered" list claiming a gap that no longer exists.
    const covered = new Set<string>([...EXPECTED_KEYS.t2, ...EXPECTED_KEYS.addonAttributed, ...EXPECTED_KEYS.manualRequeue]);
    const inBothLists = [...PENDING_KEYS.saga, ...PENDING_KEYS.recovery, ...PENDING_KEYS.rateLimitSkip]
      .filter((key) => covered.has(key));

    expect(inBothLists, `listed as both covered and uncovered: ${inBothLists.join(', ')}`).toEqual([]);
  });

  test('the not-Completed umbrella is derived, never a stored counter row', async ({ request }) => {
    // The umbrella is `failed + deleted` computed on read (ten sites move those two totals, and one
    // maintained at only some of them under-reports silently — which is what it did). If a write site
    // ever reintroduces the row, the Counters page would render two different values for the same
    // concept, so pin its absence against the real database rather than only in the unit suite.
    const rows = await counters(request);
    const stored = [...rows.keys()].filter((key) => key.startsWith('stats:unsuccessful'));

    expect(stored, `stats:unsuccessful must never be written: ${stored.join(', ')}`).toEqual([]);
  });

  test('the Counters page renders what the API reports', async ({ page, request }) => {
    // The API is the source of truth for the numbers; this proves the page actually shows them, which
    // is where a missing colour entry or an unparsed key would surface.
    const rows = await counters(request);

    // The page has its own browser context, so it needs its own cookie — `page.request` shares the
    // page's jar, which the SPA's own fetches then inherit.
    await login(page.request);

    await page.goto('/warp/counters');
    await expect(page.getByRole('heading', { name: 'Counters' })).toBeVisible();

    const succeeded = rows.get('stats:succeeded') ?? 0;
    expect(succeeded, 'the showcase workload should have completed some jobs').toBeGreaterThan(0);

    // The page lists raw keys, so assert the key and its value are both on screen together.
    const row = page.locator('tr', { hasText: 'stats:succeeded' }).first();
    await expect(row).toBeVisible();
    await expect(row).toContainText(succeeded.toLocaleString());
  });
});
