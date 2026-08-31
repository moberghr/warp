import { defineConfig } from '@playwright/test';

/**
 * LIVE end-to-end config — distinct from `playwright.config.ts`, which runs Vite in `--mode demo`
 * against mocked fixtures for screenshots. That one can never catch a metrics bug: the numbers it
 * renders are hand-written in `src/demo/data.ts`.
 *
 * This config boots the real Aspire stack (Postgres + migrator + dashboard app + worker), drives real
 * jobs through real handlers, and asserts on the counters the worker actually wrote. The precedent for
 * needing it is in tasks/lessons.md (2026-07-20): a stream-consumption bug turned every successful
 * webhook into a failure, and the whole unit suite passed because its stubs were re-readable — "a
 * runnable end-to-end demo caught in minutes what the whole unit suite missed".
 *
 * The migrator wipes and recreates the schema on every run (no WARP_DEMO_PRESERVE_DB on it), so each
 * run starts from a clean counter table. Assertions are still written as deltas around each action,
 * because the demo also registers recurring jobs that tick in the background.
 *
 * Ports are fixed because the AppHost marks these endpoints `IsProxied = false`.
 */
export default defineConfig({
  testDir: './e2e-live',

  // A single worker: these tests share one database and assert on global counter deltas, so they
  // cannot run concurrently with each other.
  workers: 1,
  fullyParallel: false,

  // Jobs have to be published, claimed, executed, retried on a delay, and finalized. That is seconds,
  // not milliseconds — and the retry-then-settle cases are the slowest.
  timeout: 180_000,
  expect: { timeout: 30_000 },

  use: {
    baseURL: 'http://localhost:5104',
    viewport: { width: 1920, height: 1080 },
    actionTimeout: 15_000,
    trace: 'retain-on-failure',
  },

  webServer: {
    // `dotnet run` on the AppHost brings up the Postgres container, runs the migrator to completion,
    // then starts the dashboard app and the worker. First run also restores + builds, hence the long
    // timeout. Waiting on /warp specifically means we do not proceed until the dashboard app is
    // serving, which the AppHost gates on WaitForCompletion(migrator).
    command: 'dotnet run --project ../demo/Warp.Demo.AppHost',
    url: 'http://localhost:5104/warp',
    // Reuse is now EXPLICIT rather than "anywhere but CI". `!process.env.CI` silently adopted whatever
    // was already serving 5104 — a stack from another branch, or one whose migrator ran against an
    // older schema — and these tests assert on counters the worker wrote, so a stale stack produces a
    // confident pass or a failure that describes the wrong code. The ports are fixed by the AppHost
    // (IsProxied = false), so a second checkout cannot simply move out of the way.
    //
    // Booting the stack takes minutes, so keeping the fast path matters — it is just opt-in now, for
    // when you know the running stack is this checkout:
    //
    //   WARP_REUSE_STACK=1 npm run test:e2e:live
    reuseExistingServer: process.env.WARP_REUSE_STACK === '1',
    timeout: 300_000,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      ASPIRE_ALLOW_UNSECURED_TRANSPORT: 'true',
    },
  },
});
