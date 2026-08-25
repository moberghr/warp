---
sidebar_position: 2
---

# Rate limits

Admin-managed rate-limit overrides. The page lists every override row — **Name**, **Count**,
**Window (s)**, **Updated** — with inline editing, deletion, and a create form.

The nav entry is hidden unless `opt.AddRateLimit()` is registered. Visiting `/ratelimits` without it shows
a card telling you which builder call is missing rather than an empty table.

## What this page is for

Rate limits are normally declared in code, with `[RateLimit]` on the request type. An override row lets you
change one **at runtime, cluster-wide, without a deploy** — the precedence is:

```
admin override row  >  persisted policy  >  the value in code
```

That ordering is the whole point: when a downstream vendor calls at 2am asking you to halve your request
rate, you edit a row here instead of shipping a build. It is also how you change a *shared* adapter limit,
since redeploying a new code value never rewrites a policy another process already persisted.

The name is the limit key — `external-api` for a `[RateLimit("external-api", …)]`, or
`warp:adapter:{name}` for an adapter's cluster-shared limiter.

## Reading the numbers

**Count** and **Window (s)** are the budget: *count* starts per *window seconds*. What happens when the
budget is gone depends on the mode declared in code, not here — `Skip` deletes the surplus job, `Wait`
reschedules it.

Live token state lives in `RateLimitBucket` rows and is not shown; this page is the *policy*, not the
meter. For consumption over time, use the Counters page — `stats:requeued-ratelimit` and
`stats:deleted-ratelimit` count what the limiter actually did.

## Keys and PII

Limit keys appear here and in job logs. Keep tenant identifiers tokenised or hashed — a raw email address
in a key becomes PII on a dashboard that defaults to open access.

## See also

- [Rate limiting](/docs/features/rate-limit) — styles (fixed, sliding, token bucket), modes, and ordering
  against the concurrency addon.
- [Concurrency limits](/docs/dashboard/runtime/concurrency-limits) — the mutex/semaphore equivalent.
