# UI UX Revamp — Plan

Branch: `worktree-ui-ux-revamp`

## Scope

Address all 10 findings from the React UI review:

1. Data layer hygiene (TanStack Query)
2. Tables (TanStack Table — sort/filter/pagination/bulk)
3. Forms (shadcn Dialog + React Hook Form + Zod)
4. Mobile-first responsive layout
5. Sidebar unification (one `EntityStateSidebar`)
6. Dashboard density (sparklines, trends)
7. Toast notifications + ErrorBoundary + skeletons
8. DetailPage decomposition
9. Design tokens (semantic colors, kill inline `bg-blue-100 dark:bg-blue-900`)
10. Accessibility pass (aria-labels, focus traps)

## Batches

### F — Foundation (sequential, must land first)
- Install: `@tanstack/react-query`, `@tanstack/react-table`, `react-hook-form`, `zod`, `@hookform/resolvers`, `sonner`, `react-error-boundary`.
- Wrap `App.tsx` with `QueryClientProvider`, `ErrorBoundary`, `<Toaster/>`.
- Add `Skeleton` + `Toast` + `Dialog` shadcn components.
- Extend `StateBadge` to use semantic tokens via CSS vars / variants (no inline `bg-*-100 dark:bg-*-900` strings).
- Add semantic color tokens to `index.css` (`--state-enqueued`, `--state-processing`, `--state-failed`, …).

### A — Data layer (depends on F)
- Add `src/api/hooks/` with `useJobs`, `useJob`, `useMessages`, `useBatches`, `useRecurring`, `useServers`, `useConcurrencyLimits`, `useRateLimits`, `useDashboardStatus`, etc.
- Replace `useState/useEffect` fetch patterns in pages.
- Wire `realtimeBus` events to `queryClient.invalidateQueries` instead of manual refetch.
- Mutations use `useMutation` + `toast.success/error` on settle.

### B — Tables (depends on F)
- `FilteredJobsTable` → TanStack Table: sort by Created, Type, State; column filters; URL-synced pagination.
- `ConcurrencyLimitsPage` / `RateLimitsPage` tables → TanStack Table.
- Bulk select + bulk delete/requeue actions.

### C — Forms (depends on F)
- shadcn `Dialog` (replace inline `{showAdd && <div>}`).
- Schemas in `src/lib/schemas/`: `concurrencyLimit.ts`, `rateLimit.ts`, `recurringJob.ts`.
- `<FormField>` wrappers with field-level errors.

### D — Layout & responsive (depends on F)
- `MainLayout` refactor: collapsible drawer on mobile (hamburger), persistent on desktop.
- Unify `JobsSidebar` / `BatchesSidebar` / `MessagesSidebar` → single `<EntityStateSidebar entity="jobs|batches|messages" counts={…} />`.
- Add breakpoints to tables (horizontal scroll wrapper + column priority on mobile).
- Global search input in navbar (server-side query against jobs/traces).

### E — Dashboard density + DetailPage decomposition (depends on F)
- Dashboard cards: add sparkline (24h trend), failure-rate %, click-through to filtered list.
- Time-range picker on `RealtimeChart` (1m / 5m / 15m / 1h).
- Split `DetailPage` into `<JobHeader>`, `<JobTimeline>`, `<JobProgress>`, `<JobLogs>`, `<RelatedJobsSection>`.
- Inline trace-graph preview (collapsed by default).
- Skeleton loaders replace spinners on initial load.

### G — Accessibility & polish (depends on D, E)
- `aria-label` on all icon-only buttons.
- Focus trap on Dialog (shadcn handles this once Dialog lands in F).
- Keyboard shortcut layer (`?` shows shortcuts, `j/k` row nav in tables).

## Sequencing

```
F (foundation) ──► [A, B, C, D, E in parallel] ──► G (a11y polish)
```

A/B/C/D/E are dispatched to fresh subagent worktrees post-F. Final merge happens in this worktree (`worktree-ui-ux-revamp`).

## Out of scope (deferred)

- Replacing Chart.js with a different library (large rewrite for unclear gain).
- Removing one of `date-fns`/`luxon` (cosmetic).
- Backend changes (search endpoint addition stays UI-only with existing API).

## Verification

- `npm run build` and `tsc -b` clean.
- `dotnet build src/Warp.slnx` clean (no backend coupling expected).
- Manual smoke: dashboard loads, job list paginates, filter works, edit form opens, dark mode toggle works, mobile breakpoint usable.
