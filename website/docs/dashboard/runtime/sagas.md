---
sidebar_position: 3
---

# Sagas

Live saga instances — one row per active saga, with **Type**, **Correlation key**, **Updated** and
**Created**, and a search box over the correlation key. Three tiles above show live sagas, started today,
and completed today.

The nav entry is hidden unless `opt.AddSagas()` is registered.

## Only live sagas appear

Warp **deletes the row when a saga completes** (`MarkCompleted()` removes it in the same `SaveChanges`).
So this page is a list of *in-flight* sagas, not a history — an empty page means everything finished, not
that nothing ran. It also means a correlation key is immediately reusable after completion.

If you need history, the counters `warp.sagas.started` / `completed` / `requeued` carry it.

## Detail page

A saga's detail shows its correlation key, current state, version, and an **activity log** of every
message that was handled against it — capped at the 200 most recent invocations, with a truncation flag
when there were more.

**Force complete** is the escape hatch for a saga wedged waiting on a message that will never arrive. It
takes the saga's mutex and emits a structured audit log line, so the intervention is traceable afterwards.
It deletes the row like a normal completion — any timeout message that lands later is silently dropped.

## Correlation keys and PII

A correlation key is visible on this page and in logs. `SagaPiiCheck` blocks `[Correlate]` properties
whose names look like PII (Email, Phone, SSN and similar) at registration time, so a saga keyed on a raw
email address fails fast rather than quietly publishing addresses to the dashboard. If the value is
genuinely anonymised, opt out with `[Correlate(IsAnonymized = true)]`.

## See also

- [Sagas](/docs/features/sagas) — defining a saga, correlation, timeouts, concurrency and storage.
