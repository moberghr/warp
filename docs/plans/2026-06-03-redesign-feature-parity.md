# Plan — Redesign Feature Parity Restoration

Spec: `docs/specs/2026-06-03-redesign-feature-parity.md`

## Batches

### Batch 1 — Job Detail restoration (largest)
**Files:**
- `src/ui/src/pages/detail/DetailPage.tsx`
- `src/ui/src/pages/detail/JobDetailStandard.tsx`
- `src/ui/src/pages/detail/FlowCard.tsx` (new — port from main)
- `src/ui/src/pages/detail/FilteredJobsTable.tsx` (new — port from main)

**Steps:**
1. Read `main:src/ui/src/pages/detail/DetailPage.tsx` to identify FlowCard, FilteredJobsTable, reported-progress, realtime hooks, and inline exception rendering.
2. Port FlowCard as standalone component, restyled with Panel/StateBadge primitives from the redesign.
3. Port FilteredJobsTable similarly.
4. In `JobDetailStandard.tsx`:
   - Render FlowCard near top.
   - Render reported-progress bars (use `reportedBars` prop).
   - For Messages (kind=2): render FilteredJobsTable using `api.getMessageJobs` / `getMessageJobCounts`.
   - For any job with `totalJobs > 0`: render batch progress bar (completed/failed split) — Message-with-children path.
   - Include mutex/`ConcurrencyKey` row in details list.
   - Inline exception rendering inside any history event that carries one.
5. In `DetailPage.tsx`: wire `useRealtimeRefetch` for JobFinalized; wire `onCountsUpdate` for live counts.

**Checkpoint:** `cd src/ui && npm run build` clean.

### Batch 2 — Jobs List restoration
**Files:**
- `src/ui/src/pages/jobs/JobListPage.tsx`
- `src/ui/src/pages/jobs/BulkActionBar.tsx`

**Steps:**
1. Per-row Requeue button on all rows (not just Failed). Wrap in ConfirmDialog.
2. Bulk Requeue → ConfirmDialog before mutate.
3. Bulk "Delete all of type" action via `useDeleteFailedJobsByType` with ConfirmDialog.
4. Replace hard-coded `PAGE_SIZE = 20` with `usePersistedPageSize` + a size picker.
5. Add "Page X of Y" indicator.
6. Append Scheduled column when `activeState === 'scheduled'`.

**Checkpoint:** `npm run build` clean.

### Batch 3 — Recurring confirmations
**Files:**
- `src/ui/src/pages/recurring/RecurringPage.tsx`
- `src/ui/src/pages/recurring/RecurringDetailPage.tsx`

**Steps:**
1. List page Trigger → ConfirmDialog ("A job will be enqueued immediately, on top of the normal cron schedule…").
2. List page Remove dialog: restore "cannot be undone" wording.
3. Detail page Trigger → ConfirmDialog.
4. Detail page Disable → ConfirmDialog with production-safety warning.

**Checkpoint:** `npm run build` clean.

### Batch 4 — Theme toggle (topbar + login)
**Files:**
- `src/ui/src/layouts/WarpTopnav.tsx`
- `src/ui/src/pages/auth/LoginPage.tsx`

**Steps:**
1. Add Moon/Sun toggle button to `WarpTopnav` using the existing `useTheme` hook.
2. Add the same toggle to `LoginPage`'s header area (or the styled equivalent).

**Checkpoint:** `npm run build` clean.

### Batch 5 — Dashboard Messages href
**Files:**
- `src/ui/src/pages/dashboard/DashboardPage.tsx`

**Steps:**
1. Change Messages StatCard `href` from `/messages/enqueued` to `/messages`.

**Checkpoint:** `npm run build` clean.

## Final verification

- `cd src/ui && npm run build`
- `dotnet build src/Warp.slnx`
- Manual smoke against the running TestApp (already on :5104).

## Post-implementation review items

- [ ] All 5 batches landed, build clean.
- [ ] Per-page check: Job Detail (Message, Batch, Failed), Jobs List, Recurring list+detail, Login, every page in dark mode via new toggle.
- [ ] No regressions vs the current redesign visual baseline.
