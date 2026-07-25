# CLAUDE.md — Hurrah.tv

This file provides guidance to Claude Code when working in this repository.

## What is Hurrah.tv?

A unified streaming queue app — one watchlist across all your streaming services. Search what's available on Netflix, Hulu, Disney+, etc., and manage a single queue.

**Status:** Phases 1–2 complete. Auth, AI curation, sentiment, episode tracking, and Azure deployment are all live.

## Architecture

Blazor WebAssembly frontend + .NET Minimal API backend. Three projects: `HurrahTv.Api`, `HurrahTv.Client`, `HurrahTv.Shared` (DTOs shared across the wire).

## Technology Stack

- Minimal API (not controllers)
- Tailwind CSS v4 (CLI build — `npm run build:css` in `HurrahTv.Client/`, run after any class/icon changes)
- No Hurrah.Core dependency — this is a standalone product

## Running Locally

Both projects must run simultaneously. Always use `dotnet watch` (not `dotnet run`) for hot reload.

Preferred flow: open `Hurrah.tv.code-workspace` in VS Code and run the `Watch All (API + Client)` task. CONTRIBUTING.md covers the full IDE setup.

Terminal-only alternative, from the repo root in two tabs/panes:

```bash
# Terminal 1 — API
cd HurrahTv.Api && dotnet watch --launch-profile https

# Terminal 2 — Client
cd HurrahTv.Client && dotnet watch --launch-profile https
```

**Ports:** API = https://localhost:7201, Client = https://localhost:7267

**TMDb API key** is in `appsettings.Development.json` (gitignored). The committed `appsettings.json` has a placeholder. See CONTRIBUTING.md's "First-time setup" for how to get the secrets file.

## Key Patterns

### API Endpoints
All endpoints are Minimal API, organized by feature in the `Endpoints/` directory — one `<Feature>Endpoints.cs` per slice. Auth issues 90-day JWTs.

### TMDb Integration
- `TmdbService.cs` handles all TMDb API calls with `IMemoryCache`
- Cache durations: search 30min, trending 1hr, providers 12hr, details 6hr
- API key stays server-side — WASM client never touches TMDb directly
- Provider IDs: Netflix=8, Prime=9, Hulu=15, Disney+=337, Paramount+=2303, Peacock=386, Max=1899, Apple TV+=350

### Data Model
PostgreSQL via Dapper (Npgsql). All tables created on startup via `DbService.InitializeAsync()` — **no migration files**, so schema changes are edits to that method's `CREATE TABLE` / `ALTER TABLE` statements and must be additive and idempotent (they re-run on every boot against live data).

### Client Architecture
- Pages call `ApiClient` service (typed HttpClient wrapper — all methods match API endpoints)
- Auth: `HurrahAuthStateProvider` + `TokenService` (JWT in localStorage) + `AuthMessageHandler` (auto-injects Bearer token)
- `HurrahTv.Shared.Models.QueueStatusOrdering` is the canonical status-ordering rule — `DisplayOrder` (`IReadOnlyList<QueueStatus>`) drives Queue tabs / QuickActions / Home watchlist sort, and `SortPriority(status)` (derived from `DisplayOrder`) gives the C# sort key that matches the Api SQL `CASE` in `DbService.GetQueueAsync`
- UI conventions (theme, state flow, self-gating predicates) live in `HurrahTv.Client/CLAUDE.md`

## Code Style

- No XML doc comments — only regular comments (`//`) when code isn't self-explanatory
- Comments start lowercase
- Prefer `Type variableName` over `var` when type isn't complex
- Pre-compute per-status/per-tab counts with `GroupBy().ToDictionary()` after data mutations — never run `Count()` per tab inside a render loop (O(N×tabs) per render)

## Testing

`dotnet test` runs via `HurrahTv.slnx`. New test projects must be referenced there. Test projects: `HurrahTv.Shared.Tests/` (pure logic), `HurrahTv.Api.Tests/` (`WebApplicationFactory<Program>` + real Postgres).

Local Postgres setup for `HurrahTv.Api.Tests` is documented in `HurrahTv.Api.Tests/CLAUDE.md`.

**When tests are required:**
- New or changed pure logic in `HurrahTv.Shared` — predicates, filters, sort keys, parsers, extension methods. This is where the worst bugs hide (#49 was a stale-date leak caught only at runtime). One named regression test per bug fix, referencing the issue number (e.g. `AvailableLater_Excludes_NextEpisode_In_The_Past` // pins #49/#70).

**When tests are NOT required:**
- Blazor components, CSS, page wiring, service-DI scaffolding. Verify these in the browser per the system rule for UI changes — tests for these surfaces cost more than they pay back.

**Factor for testability:**
When a feature mixes pure logic with Blazor state, extract the pure piece into `HurrahTv.Shared` first, then test. Reference pattern: `HurrahTv.Shared/Filters/WatchlistFilters.cs` was extracted from `Home.razor`'s inline filter logic. The helper accepts `DateTime todayUtc` so tests don't drift across midnight UTC, and `Func<QueueStatus, bool> isStatusActive` so callers compose the chip-state rule.

**TDD bias:**
Lean test-first when the rules are spec-able from the issue (date windows, filter rules). Write the test after for exploratory UI/UX work where the right shape isn't clear yet.

**Style:**
- No mocking frameworks. Plain `Assert` calls, no fluent-assertion libraries.
- Test helpers (`TvItem`, local builder methods) live inside the test class — no shared test-base machinery.
- Inject time via parameter for extracted static helpers and pipelines (see `WatchlistFilters.Apply` accepting `DateTime todayUtc`). Computed properties on model DTOs (e.g. `QueueItem.HasNewEpisode`) can't take parameters and use `DateTime.UtcNow` inline — that's acceptable for full-day windows where microsecond drift between the test's `UtcNow` and the predicate's `UtcNow` can't cross a boundary; refactor to methods only if you need to test the exact fence.
- Prefer direct `DateTime` comparisons over signed day-diff integers in predicates (see `Learnings/date-predicates-prefer-typed-comparisons.md`) — a wrong-sign value silently passes `is <= 7`.

**Plans:**
`/xplan` output for any feature touching `HurrahTv.Shared` should include a **Tests** bullet per phase. Phases that only touch Razor/CSS may skip it.

**Formatter gate before push:**
Run `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` locally before pushing any C# change — that's the exact command CI runs in `main_hurrahtv.yml`'s `Verify formatting` step. The targeted sub-commands (`dotnet format style/analyzers` with `--diagnostics`) are a strict subset and will pass locally while CI fails on rules like `IDE0305` / `IDE0330`. See `Learnings/dotnet-format-ci-runs-bare-not-targeted.md` for the full mechanism.

## Issue Tracking

Issues live in **GitHub Issues** on the [`mkerchenski/hurrah-tv`](https://github.com/mkerchenski/hurrah-tv/issues) repo. There's no project board — the label scheme is the tracker (`gh issue list --label "phase:now"` is the "In Progress" view). There's no `priority:*` scheme (`phase:*` does the work) and no `effort:*` scheme (`difficulty:*` does the work) — don't invent labels.

**Creating, labeling, or triaging an issue?** Invoke the `issue-conventions` skill for the full label scheme and body template.

**Closing the loop:** put `Closes #NN` / `Fixes #NN` keywords in the **PR description** (one per line at the bottom) — GitHub auto-closes the issue on merge to main. Squash-merge discards individual commit messages and replaces them with the PR title + body, so closes-keywords in commit bodies alone won't survive — the PR description is the canonical place. Don't manually close issues that the merge will close for you.

## Plans Directory

Design documents and implementation plans live in `Plans/` at repo root, **tracked in git** so they sync across machines and sit alongside the code — mirroring the sibling Hurrah / Centralpoint repos. See `Plans/README.md` for the full convention.

**This is a public repo, so the split is load-bearing:**
- `Plans/*.md` — **public.** Only non-sensitive technical design that maps to an **open** issue (or a genuinely evergreen architecture record). Add `**Tracking issue:** #NN` to the plan and a `Related plan: Plans/<file>.md` line to the issue (bidirectional link).
- `Plans/private/` — **gitignored.** Strategy, personal/onboarding docs, internal-process records, infra detail, and `private/archive/` (superseded plans for shipped work). If a plan names a secret, hostname, person, or competitive strategy, it goes here.

`/xplan` writes plans here before substantial work (see that skill for the plan file format); on ship, mark `Complete` (keep only if still useful as a design record) or move to `private/archive/`. Durable insight from the work goes to `Learnings/` via `/compound` — not here.

## Learnings Directory

Engineering learnings stored in `Learnings/` at repo root. Tracked in git. `/compound` covers when and how to write one.

## Deployment

- Staging auto-deploys on push to `main` (`.github/workflows/main_hurrahtv.yml`); production swap via the `/deploy` skill
- Database: Azure Database for PostgreSQL Flexible Server
- CI stamps short SHA as build version into `appsettings.json` + cache-busts CSS with `?v=SHA`

## Attribution Requirements

TMDb and JustWatch attribution must appear in the UI footer:
- "Data provided by TMDb" with link
- "Watch provider data by JustWatch" with link
