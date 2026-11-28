# Contributing to Warp

Thanks for helping. Bug reports, documentation fixes and pull requests are all welcome.

## Before you start

- **A bug or a small fix:** open a pull request directly.
- **A new feature or a change in behaviour:** open an [issue](https://github.com/moberghr/warp/issues) first, so the design can be agreed before you write the code.
- **A security problem:** do not open an issue. See [SECURITY.md](SECURITY.md).

## Set up

You need the .NET 10 SDK, Docker (the database tests start PostgreSQL and SQL Server containers) and Node.js 20 or later for the dashboard and the docs site.

```bash
dotnet build src/Warp.slnx

# All tests, about 5 minutes
dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj

# One category: NoDb, PostgreSql or SqlServer
dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-trait "Category=NoDb"

# Dashboard, on :5173
cd src/ui && npm install && npm run dev

# Docs site
cd website && npm install && npm start
```

| Folder | Contents |
|---|---|
| `src/core` | The libraries and the PostgreSQL and SQL Server providers |
| `src/ui` | The dashboard (React) |
| `src/tests` | The test suite |
| `src/benchmarks` | Benchmarks and the Hangfire comparison |
| `src/demo` | Demo applications |
| `website` | The docs site (Docusaurus) |

## What a pull request needs

- **A clean build.** Warnings are errors, and StyleCop, Roslynator, SonarAnalyzer and Meziantou all run in the build. Run `dotnet format --verbosity quiet` before you commit.
- **Tests on both databases.** Write one abstract test class with `[GenerateDatabaseTests]`; the generator produces the PostgreSQL and SQL Server versions.
- **Deterministic tests.** No `Task.Delay`, no mocked handlers, and no raising a `[TimedFact]` budget to get past a flaky test. Find the race.
- **Docs for anything a user can see.** Update the page under `website/docs`. If you add a page, add it to `website/sidebars.ts` and `website/static/llms.txt`; the docs build fails if `llms.txt` is missing a page.
- **One concern per pull request**, on a branch named like `fix/short-description` or `feat/short-description`, with commit messages in the imperative ("Add retry jitter").

## Design rules that reviews check

These are the ones that most often send a pull request back:

- Workers only fetch and execute jobs. Orchestration belongs in an `IServerTask`, not in the worker's fetch and execute path.
- No raw SQL in `Warp.Core`. Provider-specific SQL lives in the provider packages.
- Add-ons build on Core's public API. No `InternalsVisibleTo`, and no injected `IServiceProvider`.
- Production code takes time from `TimeProvider`, never `DateTime.UtcNow`.
- New enums start at 1.

The full set is in [`CLAUDE.md`](CLAUDE.md) and [`.claude/rules/`](.claude/rules), which are written for coding agents and are just as accurate for people. If you work with a coding agent, [`AGENTS.md`](AGENTS.md) is the short version.

## Licence

Warp is MIT licensed. By contributing you agree that your contribution is released under the same [licence](LICENSE).
