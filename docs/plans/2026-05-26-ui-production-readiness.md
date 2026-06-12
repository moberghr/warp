# Plan — UI Production Readiness Pass

Companion to `docs/specs/2026-05-26-ui-production-readiness.md`. Sequenced for safety-first delivery: destructive-action guardrails ship first; cosmetic polish ships last.

## Sequencing

| # | Batch | Risk | Verify |
|---|---|---|---|
| 1 | Destructive action confirmations | High value, low risk | `npm run lint && npm run build`, click Delete in dev |
| 2 | Detail-page parity + state rails | Medium | lint + build, route through `/messages/detail/:id` |
| 3 | Servers/Workers polish + types | Medium | lint + build, click Pause and confirm pending |
| 4 | Auth + logout + errors | Medium-low | lint + build, log out and back in |
| 5 | Dashboard fields + a11y | Low | lint + build, axe-devtools scan |
| 6 | Admin pages metadata | Low | lint + build, visual inspection |
| 7 | Trace page | Low | lint + build, hit `/trace/garbage-uuid` |
| 8 | Recurring (cron + TZ) | Low (adds dep) | `npm install && npm run build` |
| 9 | Services + layout chrome | Low | lint + build |
| 10 | Final `dotnet build` + review | — | full solution build, compliance-reviewer |

## Per-Batch Acceptance

- All buttons that fire mutations show a confirm dialog OR an `isPending` disabled state.
- Empty / loading / error states present on every list and detail view.
- No `aria-label`-less icon-only buttons in the touched files.
- No raw stack traces leaked in error fallbacks.
- `dotnet build src/Warp.slnx` clean after final UI rebuild (since `dist/` is embedded into `Warp.UI.dll` per the memory note).

## Risks

- **`cronstrue` dependency.** Small (~10kb), MIT-licensed, dependency-light. Approved as part of Batch 8.
- **Backend DTO drift.** `WorkerModel.serverId` may already be in the response payload but missing from the TS type — verify by reading the backend DTO before adding the field.
- **Logout flow change.** After logout, `App.tsx` needs to clear extension state and revert to `needsLogin = true`. Re-test the cold-boot probe afterwards.

## Open Decisions

None at plan time. Spec resolved every ambiguous item via the "decision:" lines in Batch 2 and Batch 5.
