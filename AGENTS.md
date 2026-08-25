# AGENTS.md — Warp

> Portable agent guidelines, generated from MTK references. AGENTS.md-aware tools (Codex, etc.) read this file.
> The authoritative source for Claude Code is `CLAUDE.md` + `.claude/rules/`.

## Project

Warp is a distributed job processing and message queue library for .NET 10. Provider-agnostic core (`Warp.Core`) plus Postgres / SQL Server providers, a worker runtime (`Warp.Worker`), a dashboard (`Warp.Dashboard`), and optional HTTP exposure (`Warp.Http`).

## Critical rules (always apply)

- **NEVER push to remote without explicit approval**, even with green CI. The engineer reviews the diff first.
- **NEVER add logic to the worker fetch/execute hot path.** New orchestration goes in an `IServerTask`, not in `WarpWorkerService`.
- **NEVER raise `[TimedFact]` budgets to fix a flake.** Use the diagnostics infra to root-cause.
- **NEVER mock handlers, sleep in tests, or spray N jobs to test concurrency.** Use `BarrierSignal` to pin handlers with N=2.
- **NEVER inject `IServiceProvider`** — inject specific deps or `IServiceScopeFactory`.
- **NEVER use `InternalsVisibleTo` for addons** — addons compose against Core's public API only.

## Build & test

- `dotnet build src/Warp.slnx`
- `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` (full suite ~1m 30s)
- By category: `-- --filter-trait "Category=NoDb"` / `Category=PostgreSql` / `Category=SqlServer`
- `dotnet format --verbosity quiet`

Build must pass with zero warnings (`TreatWarningsAsErrors=true`).

## Coding style (highlights)

- `var` for all locals; no explicit types
- Braces on all control flow, even single-line bodies
- File-scoped namespaces
- LINQ: chained methods on separate lines, `.` at start of each line, multiple `.Where()` over `&&`
- `Select` projections over `Include` for reads
- Avoid `else` — return early
- Use `string.Equals(a, b, StringComparison.X)` over `==` for strings

Full guide: `.claude/references/coding-guidelines.md`.

## Architecture (highlights)

- Everything is a `Job` with `Kind` discriminator. `ParentJobId` chain handles all relationships.
- Workers are pure executors of `Kind=Job`. Routing/orchestration lives in `IServerTask` impls driven by `ServerTaskHost<TContext>`.
- In-memory `IRequest<T>` / `IStreamRequest<T>` go through `IMediator`, never the DB.
- No raw SQL in `Warp.Core` — only in `Warp.Provider.PostgreSql` / `Warp.Provider.SqlServer`.
- Addon order matters: `AddRetry()` before `AddTimeout()`; `AddConcurrency()` before `AddRateLimit()`.

Full details: `.claude/rules/architecture.md`, `.claude/references/architecture-principles.md`.

## Testing (highlights)

- xUnit v3 (`xunit.v3.mtp-v2`), Shouldly, Moq, Respawn, Testcontainers (Postgres + MSSQL)
- `[GenerateDatabaseTests(FixtureKind.X)]` source generator emits `_PostgreSql` and `_SqlServer` subclasses — hand-write only the abstract base
- Each unit test calls one public method on one class; fresh `_fixture.CreateContext()` for arrange/act/assert
- `[TimedFact]` defaults to 10s; opt-in to longer with `[TimedFact(N_000)]`
- No `Task.Delay` except in handlers designed to be cancelled

Full guide: `.claude/rules/testing.md`.

## Git workflow

- Branches: hierarchical with `/` (`feat/`, `fix/`, `chore/`, `docs/`, `test/`, `bug/`)
- Commits: imperative mood, describe the "what"
- PRs merged via GitHub UI after review; never push to main from local

Detailed rules numbered §1.x–§8.x live in `.claude/rules/*.md`.
