# UI Redesign — Feature Parity Restoration

**Date:** 2026-06-03
**Branch:** feat/redesign
**Goal:** Restore every functional feature from `main` that was dropped in the Warp Soft System redesign, without reverting the new design system.

## Scope

Pure frontend, TypeScript/React. No backend, schema, or API changes. No new features — only restore what existed on `main` before the redesign.

## Source of truth

`feat/redesign` audit (this session) — comparison of every page under `src/ui/src/pages/` against `main`.

## Change manifest (in-scope files)

### Job Detail (largest gap)
- `src/ui/src/pages/detail/DetailPage.tsx` — wire realtime refetch, ensure props reach detail components.
- `src/ui/src/pages/detail/JobDetailStandard.tsx` — render FlowCard, child-jobs table (Messages), batch progress, reported progress bars, mutex/concurrency row, per-event exceptions.
- (new helper) `src/ui/src/pages/detail/FlowCard.tsx` — relationships card (parent, spawnedBy, continuations, spawnedJobs).
- (new helper) `src/ui/src/pages/detail/FilteredJobsTable.tsx` — child-jobs table for Messages (driven by `api.getMessageJobs` / `getMessageJobCounts`).

### Jobs List
- `src/ui/src/pages/jobs/JobListPage.tsx` — per-row Requeue for non-Failed, confirm dialogs for single/bulk requeue, page-size picker via `usePersistedPageSize`, "Page X of Y" indicator, Scheduled column on scheduled view.
- `src/ui/src/pages/jobs/BulkActionBar.tsx` — add "Delete all of type" with confirm + `useDeleteFailedJobsByType` wiring.

### Recurring
- `src/ui/src/pages/recurring/RecurringPage.tsx` — wrap Trigger in ConfirmDialog; tighten Remove warning ("cannot be undone").
- `src/ui/src/pages/recurring/RecurringDetailPage.tsx` — confirm dialogs for Trigger and Disable (production-safety warning).

### Global layout
- `src/ui/src/layouts/WarpTopnav.tsx` (or sibling) — add theme toggle (Moon/Sun) wired to existing `useTheme`.
- `src/ui/src/pages/auth/LoginPage.tsx` — add theme toggle in the login screen header.

### Minor
- `src/ui/src/pages/dashboard/DashboardPage.tsx` — change Messages card href back to `/messages` (not `/messages/enqueued`).

## Out of scope

- Reintroducing the secondary "state sub-sidebar" globally — current per-page `GroupStateRail` is the redesign's intentional replacement. Verify coverage exists on Jobs/Batches/Messages but do NOT add a global sidebar.
- Reverting Concurrency/RateLimits to inline cell-edit — the dialog form is the intentional new UX (confirmed feature-equivalent).
- Restoring the "Connection lost" top banner — status pill in `WarpStatusbar` is the redesign's intentional replacement.
- Backend, schema, API.

## Public contracts

None — purely UI restoration. No new props/APIs exposed outside the component tree.

## Security impact

None.

## Implementation batches

1. **Job Detail restoration** (biggest, most isolated)
   - Add `FlowCard.tsx`, `FilteredJobsTable.tsx` helpers (port from `main`).
   - Wire `JobDetailStandard.tsx` to render them + reported progress bars + per-event exceptions + mutex row.
   - Wire `useRealtimeRefetch` in `DetailPage.tsx`.

2. **Jobs List restoration**
   - Per-row Requeue + confirm dialog (single + bulk).
   - "Delete all of type" bulk action with confirm + hook.
   - Page-size picker + "Page X of Y".
   - Scheduled column on scheduled view.

3. **Recurring confirmations**
   - Trigger confirm (list + detail).
   - Disable confirm (detail).
   - Tighten Remove warning text.

4. **Global layout + Login theme toggle**
   - Add theme toggle button in topbar + login header.

5. **Dashboard Messages href fix**
   - One-line revert.

## Tests / verification

UI-only — no unit tests added. Verification is manual:
- `npm run build` clean (TypeScript + Vite).
- `dotnet build src/Warp.slnx` (embedded dist).
- Manual smoke against `Warp.TestApp` of: a finished batch with children, a Message detail, a Failed job (requeue), bulk delete, recurring trigger/disable, theme toggle.

## Assumptions & risks

- `main`'s implementations are the authoritative reference for visual+behavioral expectations; redesign tokens (terracotta palette, Panel, etc.) replace `main`'s Tailwind classes. Plan: copy structure, swap styling primitives.
- React-query hooks (`useDeleteFailedJobsByType`, `useRealtimeRefetch`, `usePersistedPageSize`) already exist — verify before each batch.
- `BatchDetailPage.tsx` already covers batch-side rendering; this restoration is for Message-side child rendering inside `JobDetailStandard.tsx`.

## Open decisions

None — every gap maps to a known feature on `main`.
