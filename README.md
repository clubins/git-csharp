# Scion (C#)

A server-centric version control system for agent fleets, implemented in C#. It reimplements the
model behind [Anthropic's Scion](https://www.anthropic.com/research/redesigning-source-control):
changesets in an append-only write-ahead log, all source-control operations (diff, blame, merge,
history) answered server-side, and a thin `scn` client that never clones the repository.

The goal: source control should never be the slowest step of an agent's loop, and adding parallel
agents should add no source-control contention.

## Status

Scaffold only - solution layout and test harness. Scion features arrive in later PRs; the
approved design lives in the project blueprint ("Scion in C#: a server-centric VCS for agent
fleets").

## Layout

| Area | Purpose |
|---|---|
| `src/Scion.Core` | Changeset/blob object model, SHA-256 content addressing, diff, merge, merge-base, skip-graph, derived-data engine |
| `src/Scion.Server` | ASP.NET Core minimal APIs: changeset creation, history/diff/blame queries, ref CAS, blob store |
| `src/Scion.Cli` | The `scn` thin client: init, commit, log, diff, checkout; bounded LRU cache |
| `src/Scion.Import.Git` | One-way git -> changeset import (LibGit2Sharp) |
| `src/Scion.Bench` | Performance harness: startup, commit cost, parallel-agent contention |
| `tests/Scion.Core.Tests` | Unit tests for the object model and its algorithms |
| `tests/Scion.Server.Tests` | End-to-end API tests via `WebApplicationFactory` |
| `tests/Scion.Cli.Tests` | End-to-end CLI flows against a test server |

## Build and test

Requires the .NET SDK pinned in [`global.json`](global.json) (.NET 10 LTS, `rollForward:
latestFeature`).

```bash
dotnet build
dotnet test
```

Run the server locally (once it does more than the scaffold):

```bash
dotnet run --project src/Scion.Server
```

## Conventions

- Nullable reference types enabled; warnings are errors (`Directory.Build.props`).
- File-scoped namespaces, enforced at build time via `.editorconfig` +
  `EnforceCodeStyleInBuild`.
- xunit for tests; conventional commits (`chore(scaffold)`, `ci`, `docs`, later `feat:`, `fix:`).
