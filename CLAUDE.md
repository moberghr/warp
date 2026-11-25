# Warp — Engineering Standards

> Distributed job processing and message queue library for .NET 10. Provider-agnostic core + Postgres / SQL Server providers. Ships as `Warp.Core` (includes durable outbound webhook delivery — `Warp.Core.Webhooks`, always-on, no opt-in), `Warp.Worker`, `Warp.Dashboard`, `Warp.Http` (inbound HTTP endpoints), `Warp.Adapters.Http` + `Warp.Adapters.Refit` (outbound service-call observability), `Warp.Provider.PostgreSql`, `Warp.Provider.SqlServer`.
>
> Source of truth for AI agents: this file + `.claude/rules/`. Reference docs in `.claude/references/`.

---

## Critical Rules (Always Apply)

- **§0.1 — IMPORTANT: NEVER push to remote without explicit approval.** Even with green CI. The engineer reviews the diff first.
- **§0.2 — IMPORTANT: NEVER add logic to the worker fetch/execute hot path.** New orchestration belongs in an `IServerTask`, not in `WarpWorkerService` (§6.1).
- **§0.3 — NEVER raise `[TimedFact]` budgets to fix a flake.** Use the diagnostics infra to root-cause the race; 10s is the default for a reason (§4.4).
- **§0.4 — NEVER mock handlers, sleep in tests, or spray N jobs to test concurrency.** Use `BarrierSignal` to pin handlers and assert with `N=2` (§4.7).
- **§0.5 — NEVER inject `IServiceProvider` and NEVER use `InternalsVisibleTo` for addons.** Inject specific deps (or `IServiceScopeFactory` for scope creation); addons compose against Core's public API only (§2.4, §2.11).

---

## Skill Routing

| What you need | Skill | When |
|---|---|---|
| Build a feature | `/mtk <description>` | New endpoints, addons, multi-file work |
| Quick fix | `/mtk fix <description>` | Bug fixes, 1–3 file changes |
| Pre-commit check | `/mtk review before commit` | Before every commit |

---

## Tech Stack

- **Active stack:** dotnet (`net10.0` + `netstandard2.0` for source generators)
- **Build:** `dotnet build src/Warp.slnx`
- **Test (all):** `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` (about 4–5 min locally, ~6,000 tests)
- **Test (no DB):** `... -- --filter-trait "Category=NoDb"` (~2,170 tests)
- **Test (PG):** `... -- --filter-trait "Category=PostgreSql"` (~1,910 tests)
- **Test (SQL Server):** `... -- --filter-trait "Category=SqlServer"` (~1,910 tests)
- **Format:** `dotnet format --verbosity quiet`
- **Frontend (from `src/ui/`):** `npm install && npm run dev` (Vite on :5173, proxies `/api` → :5000)

For framework-specific guidance, see `.claude/skills/tech-stack-dotnet/SKILL.md` (in plugin cache).

---

## Project Profile

- **Framework:** .NET 10 (with netstandard2.0 source generators)
- **Solution:** `src/Warp.slnx` — 21 projects across `core/`, `core/providers/`, `tests/`, `benchmarks/`, `demo/`
- **Data layer:** EF Core 10 (Postgres via Npgsql, SQL Server via Microsoft.EntityFrameworkCore.SqlServer); EFCore.NamingConventions for snake_case
- **Distributed locking:** transaction-scoped by default (`RunUnderTransactionLockAsync`, pooler-safe); Medallion `DistributedLock.Postgres` + `DistributedLock.SqlServer` behind `IWarpLockProvider` for the few session-scoped sites, which need a **session-mode** connection (§2.16). Releasing a lock never throws (§2.17)
- **Patterns:** Custom mediator (source-generated dispatch), unified `IRequest<T>` hierarchy, `IPipelineBehavior` chain, opt-in addons (§2.11); background services, queue metrics, error grouping and metrics retention tiers are base features, not addons.
- **Test stack:** xUnit v3 (`xunit.v3.mtp-v2`), Shouldly, Moq, Respawn, Testcontainers (Postgres + MSSQL), `Microsoft.AspNetCore.TestHost` for dashboard auth
- **Frontend:** Vite + React 19 + TypeScript, Tailwind + shadcn/ui, Zustand, TanStack Query (React Query) + TanStack Table, Axios
- **Analyzers (enforced as errors):** StyleCop, Roslynator, SonarAnalyzer, Meziantou (`TreatWarningsAsErrors=true` in `src/Directory.Build.props`)

---

## Domain Model — One-Sentence Refresher

Everything is a **Job** with a `Kind` discriminator (`Job=1, Message=2, Batch=3`). Messages and Batches are Jobs that spawn/group child jobs via `ParentJobId`. Workers are **pure executors** for `Kind=Job`; routing/orchestration lives in `IServerTask` implementations driven by `ServerTaskHost<TContext>`. In-memory `IRequest<T>` and `IStreamRequest<T>` go through `IMediator` and never touch the DB.

Full details in `.claude/rules/architecture.md` §2.x.

---

## Standards Reference

Detailed rules in `.claude/rules/` (auto-loaded by Claude Code):

| File | Covers | Section |
|---|---|---|
| `security.md` | Secrets, PII in logs, transactions, row locking | §1.x |
| `architecture.md` | Unified data model, worker/server-task split, addons, server context, lock shapes | §2.x |
| `architecture-infra.md` | DB push, dashboard push, PostgreSQL lock pool, database clock (path-scoped) | §2.9, §2.10, §2.18, §2.20 |
| `coding-style.md` | `var`, braces, LINQ chaining, naming, project-specific style | §3.x |
| `testing.md` | xUnit v3, `[TimedFact]`, source-generated DB tests, fixtures, integration patterns | §4.x |
| `data-layer.md` | EF Core, no raw SQL, `AsNoTracking`, `Select` over `Include`, schema | §5.x |
| `performance.md` | Worker hot path, Counter rows, signal-driven wakeup | §6.1–§6.8 |
| `performance-worker.md` | Claiming, finalization, job monitor, Orchestrator queries, cluster-task leases, perf CI (path-scoped) | §6.9–§6.25 |
| `git-workflow.md` | Hierarchical branches, imperative commits, analyzer-clean builds | §7.x |
| `project-specific.md` | Job lifecycle, cancellation, concurrency/timeout, metadata conventions, enums-from-1, notifier post-commit rule, append-only outcome stats | §8.1–§8.7, §8.11–§8.16, §8.25, §8.33 |
| `addons.md` | Rate limit + policy resolution, recurring jobs, source generators, sagas, background services (path-scoped) | §8.8–§8.10, §8.17, §8.18 |
| `observability.md` | Adapters, webhooks, endpoints, multi-app, sinks, queue metrics, client, trace, issues, retention tiers, SLOs, custom metrics, outbound adapters §2.15 (path-scoped: loads only when touching those areas) | §8.19–§8.34 |

Reference docs (read on-demand by skills and review agents):

- `.claude/references/architecture-principles.md` — 16 core principles (curated)
- `.claude/references/coding-guidelines.md` — Moberg C# coding style
- `.claude/references/quick-check-list.md` — Reviewer fast-check list
- `.claude/references/design-notes/` — full rationale, history, measurements and rot-guard lists behind the condensed §2.x, §6.x and §8.x rules (one file per section)

---

## Build & PR Hygiene

- **Tests on both DBs:** every new behavior asserts on both Postgres and SQL Server via `[GenerateDatabaseTests]` source generator.
- **Build must be analyzer-clean** — `TreatWarningsAsErrors=true` is non-negotiable.

<!-- mtk-setup: v7.10.2
     coding-guidelines: moberghr/coding-guidelines@4043387ca2c70ed0cd76e005861f5c471908c3bb
     generated: 2026-06-03T00:00:00Z -->
