# Metrics retention tiers

Warp folds every time-series metric — per-type/handler execution (`jobstat`), queue-wait (`qwait`), adapter and endpoint call stats, browser events, error-group trends — through one pipeline: hot-path code writes `Counter` rows, a `CounterAggregator` sums them into durable `Statistic` rows, and the dashboard reads those back. **Metrics retention tiers** control how that time-series data ages: recent data is kept at fine (5-minute) resolution, and a background task progressively **rolls it up** into hourly then daily buckets as it ages — so recent graphs stay detailed while long history stays cheap.

## The model

Every bucketed key carries an explicit resolution marker and a stamp:

```
fine   (5-min): {family}:…:hist:{token}:m5:2026-08-02-14-25
hourly (1-hr):  {family}:…:hist:{token}:h1:2026-08-02-14
daily  (1-day): {family}:…:hist:{token}:d1:2026-08-02
```

Latency percentiles get the same treatment under a `pcth` marker, so a windowed p95/p99 can be computed over any tier. **Lifetime totals** (`…:{token}` with no date) and the **lifetime `pct`** histogram are untouched — they accumulate forever and back the all-time dashboard numbers.

The write path emits at the **fine** tier (same number of counter rows as before — just a finer stamp), and the `StatisticRollup` server task does the rest:

1. **fine → hourly** — 5-minute buckets older than `FineResolutionRetention` (default 6h) are summed into their hour and deleted.
2. **hourly → daily** — hourly buckets older than `HourlyStatisticsRetention` (default 7d) are summed into their day and deleted.
3. **prune** — daily buckets older than `DailyStatisticsRetention` (default 90d) are deleted.

The roll **sums into the coarser bucket before deleting the finer one**, inside the task's lock transaction — so a crash mid-run can never double-count, and no detail is dropped (unlike the old prune, which simply deleted expired hourly rows). It runs on `StatisticRollupInterval` (default 10 minutes), off the worker hot path.

## Why it exists

- **Bounded storage** — instead of hourly buckets accumulating (or being deleted outright), each dimension keeps a fixed number of buckets: a few hours of 5-minute, a few days of hourly, then daily. Old history is retained coarsely rather than lost.
- **Sub-hour resolution** — recent data at 5-minute granularity is what short-window queries (and SLO fast-burn alerting) need; nothing finer than an hour existed before.
- **One implementation for every family** — the rollup keys purely on the trailing tier suffix, so it downsamples `jobstat`, `qwait`, adapter, endpoint, client-event, and error-group trends alike, and **migrates every pre-existing hourly row** to the new scheme automatically.

## Configuration

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();

    opt.FineResolutionMinutes = 5;                              // fine bucket width; 60 emits hourly directly
    opt.FineResolutionRetention = TimeSpan.FromHours(6);        // fine → hourly age
    opt.HourlyStatisticsRetention = TimeSpan.FromDays(7);       // hourly → daily age
    opt.DailyStatisticsRetention = TimeSpan.FromDays(90);       // daily prune age; null keeps daily forever
    opt.StatisticRollupInterval = TimeSpan.FromMinutes(10);     // how often the rollup runs
});
```

Setting `FineResolutionMinutes = 60` makes the write path emit hourly buckets directly, effectively turning the fine tier off; the rollup then only does hourly → daily. Leaving `DailyStatisticsRetention = null` keeps the coarse daily history indefinitely.

## Migration

**No schema change and no migration** — every tier lives in the existing `Statistic` / `Counter` tables as new key-prefixed rows. Pre-3.10 keys (bare `…:yyyy-MM-dd-HH`) are recognized as hourly and migrated to daily by the rollup on their normal schedule, so upgrading is transparent. The dashboard's counters and stats-history graphs read across the tiers unchanged.
