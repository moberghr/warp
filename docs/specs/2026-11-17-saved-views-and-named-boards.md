# Saved views and named metric boards

- **Date:** 2026-11-17
- **Status:** implemented, except Part 2 Step B. Step B (the saved view store and the Views menu) was built, then removed on 2026-11-19: named boards cover the use case. Part 1 (named boards) and Part 2 Step A (URL state, so every page can be shared as a link) remain, and the `saved_view` table below no longer exists.
- **Scope classification:** new feature, additive. Nothing changes for a host that does not use it, except that existing board tiles are shown on a board named "Default".
- **Security impact:** low. Stores what the operator types (names, a query string) and the acting dashboard user's name (§1.2 allows the actor name in audit lines). No payloads, no tag values beyond what the operator put in a filter.
- **Schema impact:** additive. Two new tables (`metric_board`, `saved_view`) and one nullable column (`metric_board_tile.board_id`). Hosts pick them up with `dotnet ef migrations add`.
- **Delivery:** on `feat/metrics-follow-ups`, merged together with the follow-ups.

## Problem

Warp has one global health board and no way to save a view. Every team shares the same 48 tiles, and a carefully set-up metric chart (instrument, application, breakdown, filters, overlays, percentile, rate) is lost when the tab closes. Breakdown, filters, compare and percentile are not even in the URL, so a chart cannot be shared as a link. Kibana and Grafana users expect both named dashboards and saved searches.

## Decisions

| Decision | Choice |
|---|---|
| Storage | The host's database, `warp` schema, always-in-schema tables mirrored by `WarpServerContext` (§2.11, §2.14). Not browser storage, which is per machine and cannot be shared. |
| Ownership | Author = the signed-in dashboard user (`DashboardUser.Name`), as for annotations. A view is **personal** (listed only to its author) or **shared** (listed to everyone). Boards are always shared. On an open dashboard every author is `anonymous`, so the UI defaults new views to shared there. |
| What a saved view stores | The page id plus its URL query string, unchanged. One mechanism for every page; no per-page schema. |
| Boards | Named boards with their own tiles. Existing tiles move to a seeded board named `Default`. |
| Code seeding | `opt.AddMetricBoard("Checkout", b => ...)` seeds a named board, insert-if-absent like today. The existing `AddMetricBoard(b => ...)` overload keeps working and targets `Default`. Saved views are not seeded from code (they are personal by nature); revisit if asked. |
| Retention | Kept forever; nothing sweeps them. |
| Not building | Kibana-style free-form dashboards (arbitrary visualisations, drag layout, per-panel queries). Grafana covers that for teams on the OTel sink. |

## Part 1: named boards

### Data model
- **`MetricBoard`** (`Warp.Core.Data.Entities`): `Id`, `Name` (≤ 200, unique, case-insensitive match in memory like the SLO seeder), `Description?` (≤ 500), `Position`, `Application?` (≤ 200, the default scope shown in the picker; does not filter tiles), `CreatedAt`, `UpdatedAt`, `CreatedBy?`.
- **`MetricBoardTile.BoardId`**: nullable int, soft reference (no FK, like `SloDefinitionId` on annotations, so a provider-specific cascade is not needed). Null reads as the `Default` board, so existing rows need no data migration. Index `(BoardId, Position, Id)` replacing `(Position, Id)`.
- **`Default` board**: created on first use by the board command service, or by the seeder, under the existing `warp:metric-board` transaction lock. Its id is stored; tiles with `BoardId == null` are treated as belonging to it.
- **Caps**: 48 tiles per board (unchanged), 100 boards.

### API
- `GET {prefix}/api/metrics/boards` lists boards (id, name, description, position, tile count).
- `POST {prefix}/api/metrics/boards` creates or renames (404 when editing a deleted board, like tiles). `DELETE {prefix}/api/metrics/boards/{id}` deletes the board **and its tiles** in one `SaveChanges`; the `Default` board cannot be deleted (400).
- `GET {prefix}/api/metrics/board?board={id}&from=&to=`: `board` optional, default `Default`. The batched read is unchanged; it filters tiles by board first.
- Tile upsert gains `boardId` (default `Default`). Moving a tile between boards is an edit of `boardId`.
- **Verdict**: `GET .../applications/verdict?...&board={name}` optional. Without it the verdict keeps today's behaviour (tiles of every board). With it, only that board's tiles count; SLOs are unaffected. Name, not id, because a CI script knows the name.

### Code
```csharp
opt.AddMetricBoard("Checkout", b => b
    .Ratio("Checkout success", "shop.checkout.completed", "outcome", "ok", "error,declined",
        application: "shop-api", check: MetricTileCheck.AtLeast(0.98))
    .Value("Checkout 5xx", "http.server.request.duration",
        tagFilter: "http.route=api/checkout*,http.response.status_code=5*",
        application: "shop-api", check: MetricTileCheck.AtMost(5)));

opt.AddMetricBoard(b => b.Value(...));   // existing overload: the Default board
```
Seeding stays insert-if-absent and runs under `warp:metric-board`. A seeded tile matches an existing tile on the same board by name or by its fields, as today; the board itself matches by name.

### UI
- Board picker (select + "New board", "Rename", "Delete") on **Metrics → Board**. The selected board lives in the URL (`/metrics/board?board=checkout`, by a URL-safe id of the name) and, as a convenience, in `localStorage` as the last board opened.
- The tile editor gains a "Board" select so a tile can be moved.
- Demo fixtures: three boards (Default, Checkout, Payments).

## Part 2: saved views

### Step A: every metric control in the URL
Today only `range`, `from`/`to` and `rate` are in the URL. Add the rest of `MetricControls`: `application`, `breakdown`, `filter` (repeated `key:value`, same syntax as the API), `compare` (`1d,7d`), `percentile`, `view` (`values|ratio`), `numerator`, `minmax`. `parseControlParams`/`writeControlParams` replace `parseRangeParams`/`writeRangeParams` and round-trip every field. Defaults are left out of the URL so plain links stay short. This is useful on its own: any chart becomes a shareable link.

The Issues and Jobs list pages keep their filters in component state today. Move them into the URL the same way (Issues: source, status, application, show 4xx; Jobs: state, queue, type, application, search). These pages then work with saved views without extra code.

### Step B: the saved view store
- **`SavedView`** (`Warp.Core.Data.Entities`): `Id`, `Name` (≤ 200), `Page` (≤ 64, from an allowlist: `metric`, `issues`, `jobs`, later others), `Target?` (≤ 256: the instrument name for a metric view; null for list pages), `Query` (≤ 2000, the URL query string without `?`), `Shared` (bool), `Author` (≤ 200), `CreatedAt`, `UpdatedAt`. Index `(Page, Target)` and `(Author)`.
- **Validation** (`SavedViewValidation`, pure): page in the allowlist, name non-blank, lengths bounded, `Query` parsed as a query string with only the keys that page knows (unknown keys rejected, so a view can never smuggle an arbitrary URL). Caps: 200 views per author, 500 shared.
- **Visibility**: a read returns the caller's own views plus every shared view. Only the author may edit or delete a personal view; anyone may edit a shared view (the dashboard is already gated by the host's auth, §1.5). Edits keep the original author and log the editor.
- **Audit**: structured `LogInformation` `Saved view {Id} created|updated|deleted by {Actor} on {Page}`; never the query.

### API
- `GET {prefix}/api/views?page=&target=` lists visible views, newest first.
- `POST {prefix}/api/views` creates or updates (400 validation, 403 editing someone else's personal view, 404 deleted).
- `DELETE {prefix}/api/views/{id}` (403/404 as above).
- Author always comes from the principal, never the body.

### UI
- A "Views" menu in the page header of Metrics detail, Issues and Jobs: **Save view** (name, Shared toggle, defaults to shared on an open dashboard), the list split into "Mine" and "Shared", and **Update** / **Rename** / **Delete** for the active view. Choosing a view navigates to `{page}?{query}`.
- The metric list shows a small "saved views" count per instrument.
- Demo fixtures for each page.

## Services and registration
- `IMetricBoardQueryService`/`IMetricBoardCommandService` extended for boards; new `ISavedViewQueryService`/`ISavedViewCommandService`. All registered by `AddWarp`, so a dashboard-only host serves them (§8.34 pattern).
- Entities mapped in `ServiceConfiguration` beside `MetricBoardTile` and `MetricAnnotation`, enums from 1 (§8.11), storage pinned by `ApplyWarpModel` (§5.12).

## Tests
- **Both providers** (`[GenerateDatabaseTests]`):
  - Boards: CRUD, the Default board created once under an N=2 `BarrierSignal` race, deleting a board removes only its tiles, legacy `BoardId == null` tiles appear on Default, the board read filters by board, the 48/100 caps, the seeder by name across two boards, the verdict `board` parameter (counts only that board's tiles; unknown board 404).
  - Saved views: CRUD, visibility (author vs other user vs shared), 403 on someone else's personal view, unknown query keys rejected, caps, author from the principal, dashboard-only host.
  - Topology and `HostDbContextCoexistenceTestsBase` (new tables, nullable `board_id`).
- **NoDb**: `SavedViewValidation` per branch; query-key allowlists per page.
- **vitest**: `parseControlParams`/`writeControlParams` round-trip for every control (table test over every `MetricControls` field), the Issues/Jobs URL state, the views menu model, demo fixtures.

## Docs
- `custom-metrics.md`: "Named boards" and "Saved views and shareable links".
- `applications.md`: the verdict `board` parameter.
- Demo shop guide: a Checkout board and a saved "Checkout errors by version" view in `metrics-and-slos.md`.
- §8.34 in `.claude/rules/project-specific.md`.

## Batches
1. **URL state** for the metric page, Issues and Jobs (UI only, no schema). Shippable alone.
2. **Named boards**: entity, migration path, services, routes, seeding overload, verdict `board` parameter, UI picker.
3. **Saved views**: entity, validation, services, routes, views menu on the three pages.
4. **Docs and demo**, then a full review round.

## Resolved questions (2026-11-17)
1. **Saved views from code:** no. Views are created in the dashboard only.
2. **Verdict without `board`:** counts the tiles of every board, still filtered by application scope, exactly as today. Counting only the Default board would silently drop tiles from an existing CI check the moment a team moves them to a named board. `board=<name>` narrows the check.
3. **Deleting a shared view:** anyone. Access is already gated by the dashboard's own authorization (§1.5); the delete is audit-logged with the actor.
