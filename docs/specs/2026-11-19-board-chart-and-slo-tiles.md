# Chart and SLO tiles on metric boards

- **Date:** 2026-11-19
- **Status:** approved 2026-11-19
- **Scope classification:** new feature, additive. Existing Value and Ratio tiles, the board read and the verdict are unchanged.
- **Security impact:** low. A chart tile stores what the operator configured on the metric page (instrument, breakdown, filters); an SLO tile stores an objective id. No payloads.
- **Schema impact:** additive. Two nullable columns on `metric_board_tile` (`chart_query`, `slo_definition_id`). Hosts pick them up with `dotnet ef migrations add`.
- **Delivery:** on `feat/metrics-follow-ups`, merged with the follow-ups.

## Problem

Named boards are the dashboard, but a tile is a single number with a status check. A team cannot put the chart it built on the metric page (breakdown by version, filters, compare to last week, p95) on its board, and cannot see its SLOs there at all. Saved views were the first answer and were removed (2026-11-19): a view was one page's settings and gave no single screen. This puts both on the board.

## Decisions

| Decision | Choice |
|---|---|
| Tile kinds | Two new `MetricTileKind` values: `Chart = 3`, `Slo = 4` (§8.11, appended). Value and Ratio unchanged. |
| What a chart tile stores | The metric page's control query (the URL state from batch 1 of the boards spec: `breakdown`, `filter`, `compare`, `percentile`, `view`, `numerator`, `minmax`, `rate`, `application`) **without** the range. The board's range drives every tile, so one range picker moves the whole board. One grammar for the page, a shared link and a tile; "Open in chart" is the page path plus that query. |
| How a chart tile renders | The same chart component as the metric detail page, fed by the existing `GET metrics/{id}/series`. No new read API and no server-side chart logic. |
| What an SLO tile shows | The objective's current evaluation: state chip (incl. Acknowledged / NoData), attainment against target (`sloDisplay.formatObserved`/`formatTarget`), error budget remaining, short/long burn rate, last evaluated. Links to the SLO detail page. There is no SLO evaluation history (`SloEvaluation` is 1:1), so no SLO trend chart; that would need a history table and is out of scope. |
| Status | A chart tile has **no** status rule (it is a picture, not a check), so it never counts in the deploy verdict. An SLO tile shows the objective's own state; it adds no rule of its own. |
| Layout | A chart tile is wide (2 grid columns on desktop, full width on mobile); Value, Ratio and SLO tiles stay 1 column. Order by `Position` as today, with the existing move up/down. No drag layout, no per-tile sizes (still not building Grafana). |
| Pinning | "Add to board" on the metric detail page (takes the current controls, asks for a tile name and a board) and on the SLO detail page. This is the main way tiles get made; the tile editor can also create and edit both kinds. |
| Code seeding | `b.Chart("Checkout by version", "shop.checkout.completed", breakdown: "service.version", filter: "outcome=error", ...)` and `b.Slo("Checkout success", application: "shop-api")`. Insert-if-absent under `warp:metric-board`, as today. |

## Data model

- **`MetricBoardTile.ChartQuery`** (`string?`, ≤ 2000): Chart tiles only, cleared for every other kind by validation.
- **`MetricBoardTile.SloDefinitionId`** (`int?`): Slo tiles only. Soft reference, no FK (the `MetricAnnotation.SloDefinitionId` pattern).
- **`Instrument`** stays required for Value, Ratio and Chart; for an Slo tile it is stored as `""` (the column is non-null; no migration of it).
- Every Value/Ratio-only column (`RatioTag`, `GoodValues`, `BadValues`, `GaugeAggregate`, every status-rule column) is cleared for Chart and Slo tiles, the same "clear what the kind does not use" rule `SloValidation` applies.

## Validation (`MetricBoardValidation`, pure)

- **Chart:** instrument non-blank and ≤ 256; `ChartQuery` parsed as a query string with only the metric page's control keys (the server mirrors the UI's `metricQueryKeys`, with a NoDb sync test reading `metricModel.ts`, the pattern removed with saved views, restored for this one list); range keys (`range`, `from`, `to`) rejected; length ≤ 2000; filters parsed with the one `MetricFilter` grammar. Status rule must be None.
- **Slo:** `SloDefinitionId` must name an existing objective at write time (404-style 400 `"objective not found"`). Status rule must be None.
- **Caps:** 48 tiles per board (unchanged) of which at most **12 chart tiles**: each chart is its own series request every refresh, while Value/Ratio tiles share one batched read.

## Read path

- **Board read (`GET metrics/board`)**: unchanged batched read for Value/Ratio. Chart tiles come back with their stored query and no computed values. Slo tiles are joined to **one** `ISloQueryService.GetObjectives` call (no per-tile query) and carry the objective's name, kind, target, state, budget, burns and `lastEvaluatedAt`, or `missing: true` when the objective was deleted.
- **Chart series**: the UI issues one `GET metrics/{id}/series` per chart tile with the board's `from`/`to` plus the tile's query, with the metric page's refetch cadence (`seriesRefetchMs`). Requests start only for tiles in view (IntersectionObserver), so a board scrolled to the top does not fetch twelve charts.
- **Deleting an SLO** deletes the tiles pointing at it in the same `SaveChanges` (`SloCommandService.Delete` already deletes its annotations that way), so a board never shows a dead objective in the normal path; `missing` covers a delete made by an older binary mid rolling deploy.

## Verdict

Chart tiles have no status rule, so they are never counted. Without `board`: unchanged. With `board=<name>`: the board's status-rule tiles plus **only the SLOs that have an Slo tile on that board** (still application-scoped and enabled; resolved question 1). The objective ids come from the same board read, so the verdict still adds no query.

## Code

```csharp
opt.AddMetricBoard("Checkout", b => b
    .Ratio("Checkout success", "shop.checkout.completed", "outcome", "ok", "error,declined",
        application: "shop-api", check: MetricTileCheck.AtLeast(0.98))
    .Chart("Checkout errors by version", "shop.checkout.completed",
        application: "shop-api", breakdown: "service.version", filter: "outcome=error*", compare: "7d")
    .Chart("Checkout latency p95", "http.server.request.duration",
        application: "shop-api", filter: "http.route=api/checkout*", percentile: 95)
    .Slo("Checkout success", application: "shop-api"));
```

`Chart(...)` takes typed optional parameters and builds the stored query itself, so a typo is a compile error and the seeded query is always valid. `Slo(name, application)` resolves the objective by name (OrdinalIgnoreCase) and application at seed time, after the SLO seeder (seeding order: SLOs first, then boards). No match, or more than one, logs at **Error** and skips that tile, like the named-seed skip on the boards seeder. A seeded chart tile matches an existing tile on the same board by name or by (instrument, query); an SLO tile by name or by `SloDefinitionId`.

## UI

- **Board page:** renders Chart tiles with the metric chart component (compact: legend collapsed, no controls; title links to the metric page with the tile's query + the board range); Slo tiles as a status card. Range picker unchanged and drives every tile.
- **Tile editor:** a kind select (Value / Ratio / Chart / SLO). Chart: instrument picker plus the same controls as the metric page, with a live preview. SLO: objective picker.
- **Metric detail page:** "Add to board" button (board select + name, defaults to the instrument's display name). **SLO detail page:** the same.
- **Demo fixtures:** the Checkout board gains two chart tiles and an SLO tile; Payments gains one SLO tile.

## Tests

- **Both providers (`[GenerateDatabaseTests]`):** CRUD of Chart and Slo tiles; clearing of unused columns per kind; the 12-chart cap; Slo tile to a missing objective → 400; board read joins SLO state with one objective read and flags a missing one; deleting an SLO deletes its tiles; seeder for `Chart`/`Slo` (match by name and by fields, unknown / ambiguous objective skipped and logged); the verdict ignores Chart and Slo tiles; topology and `HostDbContextCoexistenceTestsBase` (two new columns).
- **NoDb:** chart-query validation per branch (unknown key, range key, bad filter, length); the metric-key sync test against `metricModel.ts`; the builder's query generation.
- **vitest:** chart tile query ⇄ controls round trip, "Add to board" building the tile from the current URL, SLO tile display (every state incl. NoData, Acknowledged, missing), lazy fetch only for visible charts, demo fixtures.

## Docs

- `custom-metrics.md`: "Named boards" gains Chart and SLO tiles and "Add to board".
- `slo-error-budget.md`: SLOs on a board.
- Demo shop guide (`metrics-and-slos.md`): the Checkout board with its chart and SLO tiles.
- §8.34 in `.claude/rules/project-specific.md`.

## Batches

1. **Chart tiles:** kind, column, validation, seeder overload, board page rendering, tile editor, "Add to board" on the metric page.
2. **SLO tiles:** kind, column, board-read join, SLO delete cascade, seeder overload, card, "Add to board" on the SLO page.
3. **Docs and demo**, then a full review round.

## Resolved questions (2026-11-19)

1. **Verdict with `board=<name>`:** counts only the SLOs that have a tile on that board (still filtered by application scope and `Enabled`), plus that board's status-rule tiles. A board with no SLO tiles counts no SLOs. Without `board`, unchanged: every SLO of the application. A behaviour change from the boards batch on this branch, never released, but it goes in the release notes because a CI step pinned to `board=` reads a different check set.
2. **Chart tile cap:** 12 per board.
3. **SLO history:** out of scope; a follow-up issue.
