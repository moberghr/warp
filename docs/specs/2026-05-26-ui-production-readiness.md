# Spec — UI Production Readiness Pass

- **Date:** 2026-05-26
- **Slug:** `ui-production-readiness`
- **Scope:** substantial-feature (UI-only, ~9 batches)
- **Security impact:** low (logout wiring + error-message sanitization)
- **Source:** 8-subagent audit across every menu item — consolidated into BLOCKER / NICE-TO-HAVE punch list.

## Goal

Close the **production-readiness gaps** found by the menu-by-menu UI audit so the dashboard can ship. Pure frontend work in `src/core/Warp.UI` (a.k.a. `src/ui/`). No backend changes — anything that needs a new endpoint is explicitly out of scope.

## Non-Goals

Items the audit flagged that are **features**, not fixes — deferred:

- Server cordon / drain / restart — no backend endpoints (`Endpoints/WarpEndpoints.cs` only has pause / resume).
- Settings page expansion beyond date format — no other settings exist yet.
- "Forgot password" recovery flow — no backend recovery endpoint.
- Concurrency / Rate Limits **live bucket state** — backend query exists for buckets, but full visualization is its own feature.
- Statistic-aggregation explainer copy on Counters page — backend behavior, not a UI fix.

## Batches

Each batch is independently mergeable; ordering is by risk (safety-first).

### Batch 1 — Destructive action confirmations
Every Delete / Bulk-delete / Cancel that currently fires on a single click gets wrapped in `<ConfirmDialog>` (the component used by `RateLimitsPage`).

**Files:**
- `src/ui/src/pages/jobs/JobListPage.tsx`
- `src/ui/src/pages/jobs/BulkActionBar.tsx`
- `src/ui/src/pages/detail/JobDetailStandard.tsx`
- `src/ui/src/pages/detail/JobDetailBold.tsx`
- `src/ui/src/pages/recurring/RecurringPage.tsx`
- `src/ui/src/pages/recurring/RecurringDetailPage.tsx`
- `src/ui/src/pages/batches/BatchDetailPage.tsx`

### Batch 2 — Detail-page parity & state-rail alignment
- `DetailPage` routes `kind=Message` to a Message-aware detail (currently falls through to `JobDetailStandard`). Decision: extend `JobDetailStandard` to surface "Child jobs" + parent-level requeue/delete when `Kind=Message` — cheapest path; no new page file.
- `BatchDetailPage:39` — map `State.Deleted → 'deleted'` CSS class (currently `'failed'`).
- `BatchJobsTable.tsx:249` — remove the dead `MoreHorizontal` button (no actions wired and out-of-scope to add).
- `GroupStateRail.tsx` — align state lists with `TAB_ORDER`: Messages add `awaiting, scheduled, deleted`; Batches add `enqueued, scheduled`.

**Files:**
- `src/ui/src/pages/detail/DetailPage.tsx`
- `src/ui/src/pages/detail/JobDetailStandard.tsx`
- `src/ui/src/pages/batches/BatchDetailPage.tsx`
- `src/ui/src/pages/batches/BatchJobsTable.tsx`
- `src/ui/src/components/v2/GroupStateRail.tsx` (path TBD when discovered)

### Batch 3 — Servers / Workers polish
- Disable Pause/Resume buttons during in-flight mutation (`ServersPage`, `ServerDetailPage`, worker group pause).
- Add "Inactive" badge on `ServerDetailPage` to match `ServersPage`.
- Add `serverId` and `serverName` to `WorkerModel` in `types/index.ts` (also extend backend DTO if needed — likely already present).

**Files:**
- `src/ui/src/pages/servers/ServersPage.tsx`
- `src/ui/src/pages/servers/ServerDetailPage.tsx`
- `src/ui/src/types/index.ts`

### Batch 4 — Auth, logout, error fallbacks
- Add `logout()` API call → `POST /auth/logout`.
- Add visible logout button (sidebar footer or topbar user menu) — visible only when `config.hasBuiltInLogin`.
- `LoginPage.tsx:182-183` — fall back to "dev build" when `import.meta.env.VITE_APP_*` is missing instead of rendering literal `undefined`.
- `RootErrorFallback.tsx:19-20` — hide raw stack trace by default (collapsible "Show details").

**Files:**
- `src/ui/src/api/index.ts` (add `logout`)
- `src/ui/src/App.tsx` (logout reverts to login state)
- `src/ui/src/layouts/WarpSidebar.tsx` (button)
- `src/ui/src/pages/auth/LoginPage.tsx`
- `src/ui/src/components/RootErrorFallback.tsx`

### Batch 5 — Dashboard hidden fields + a11y + skeleton
- Render the dashboard error state (currently swallowed at `DashboardPage.tsx:42`).
- Add `aria-label`s to icon-only range buttons in `ThroughputChart.tsx:117-130` and `PulseDot:93`.
- Complete History chart skeleton in `DashboardSkeleton.tsx:23-30`.

**Note:** "Unmapped DashboardStatistics fields" — keep dashboard layout. Just surface `databaseConnection` as a connectivity pill (the most operationally-important missing field). Other fields are already shown on dedicated pages.

**Files:**
- `src/ui/src/pages/dashboard/DashboardPage.tsx`
- `src/ui/src/pages/dashboard/ThroughputChart.tsx`
- `src/ui/src/components/skeletons/DashboardSkeleton.tsx`

### Batch 6 — Admin pages metadata
- `ConcurrencyLimitsPage` — add a "Kind" column (Mutex vs Semaphore) inferred from key shape: `warp:concurrency:k` → Mutex, `warp:concurrency:k:N` → Semaphore. If API doesn't expose enough info, label "Limit ≥ 2 = Semaphore" heuristically.
- `RateLimitsPage` — add a "Style" column (Fixed vs Sliding) and a "Policy" column (Skip vs Wait) if backend exposes them; otherwise add an info icon explaining policy is per-handler.
- `CountersPage` — replace blank canvas during history fetch with proper skeleton.

**Files:**
- `src/ui/src/pages/concurrency/ConcurrencyLimitsPage.tsx`
- `src/ui/src/pages/ratelimits/RateLimitsPage.tsx`
- `src/ui/src/pages/counters/CountersPage.tsx`

### Batch 7 — Trace page
- Empty `jobs` array → "Trace not found" empty state.
- Add "Back to Jobs" link in header.
- Validate UUID input (regex), show 400-style empty state on garbage.

**Files:**
- `src/ui/src/pages/trace/TracePage.tsx`

### Batch 8 — Recurring (cron + TZ)
- Add `cronstrue` dependency (or inline description function) → human-readable cron in list + detail.
- Add TZ suffix to "Next Execution" timestamps (use `Intl.DateTimeFormat().resolvedOptions().timeZone`).
- Add subtle footer note "History capped to last 100" on `RecurringDetailPage` history panel.

**Files:**
- `src/ui/package.json` (cronstrue dep)
- `src/ui/src/pages/recurring/RecurringPage.tsx`
- `src/ui/src/pages/recurring/RecurringDetailPage.tsx`

### Batch 9 — Background Services + layout chrome
- `BackgroundServices/Detail.tsx:495` — `LeaseCountdown` `useEffect` cleanup.
- Add log-empty hint: "(server may rate-cap noisy events)".
- `WarpStatusbar.tsx:31` — show "offline" pill when `/api/info` fetch fails.
- `MainLayout.tsx:123` — add "Retry" button on connection-error banner.

**Files:**
- `src/ui/src/pages/BackgroundServices/Detail.tsx`
- `src/ui/src/layouts/WarpStatusbar.tsx`
- `src/ui/src/layouts/MainLayout.tsx`

## Out of Scope (in addition to Non-Goals above)

- Backend changes (new endpoints, new DTOs)
- `dotnet build` is required because the UI `dist/` is embedded into `Warp.UI.dll` — but no .cs file is edited.
- New tests for UI changes — Warp does not currently have a UI test harness; visual smoke via `npm run dev` only.
- Refactor of `DetailPage`/`JobDetailStandard` beyond what's strictly needed for Message parity.

## Public Contracts

None added or removed. UI-only.

## Open Decisions

None — every batch has a concrete instruction.

## Verification

After each batch:
- `cd src/ui && npm run lint` clean
- `cd src/ui && npm run build` clean

After all batches:
- `cd src/ui && npm run build && cd ../.. && dotnet build src/Warp.slnx`
- Manually run `npm run dev` and click through every menu item (no automated harness).
