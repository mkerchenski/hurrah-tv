# Copilot instructions — Hurrah.tv

Hurrah.tv is a unified streaming-queue app: one watchlist across all streaming services. When reviewing a PR, apply the project conventions below. Flag violations; don't restate them as praise.

## Architecture

Blazor WebAssembly (standalone) frontend + .NET 10 Minimal API backend. Three projects:

- **HurrahTv.Api** — Minimal API (not controllers). TMDb proxy, auth, PostgreSQL via Dapper/Npgsql.
- **HurrahTv.Client** — Blazor WASM. UI runs in the browser; no client-side state store — state lives on the server and the client fetches on page load.
- **HurrahTv.Shared** — DTOs and pure logic shared across the wire. The contract layer.

State flows server → client. The WASM client never calls TMDb directly; the API key stays server-side.

## What to focus on in review

### Correctness (highest priority)
- **Authorization scoping.** Every user-data SQL mutation must be scoped by the authenticated `UserId` (e.g. `WHERE Id = @Id AND UserId = @UserId`). A missing `AND UserId =` clause is a high-severity finding — it lets one user mutate another's data.
- **Transaction / lock coverage.** Postgres advisory locks (`pg_advisory_xact_lock`) only cover work that runs *before* `CommitAsync()`. A mutation that the lock's intent implies should be covered must run inside the transaction and be enlisted (pass the `NpgsqlTransaction`), or Npgsql throws / the work escapes the lock.
- **Idempotent schema migrations.** Tables/columns are created on startup in `DbService.InitializeAsync` — there are no migration files. Any schema change must be idempotent (`CREATE TABLE IF NOT EXISTS`, `ALTER TABLE … ADD COLUMN IF NOT EXISTS`) and consider backfill for existing rows.
- **WASM is single-threaded.** Don't flag missing locks/thread-safety on client code, and don't suggest server threading patterns there. Conversely, `DateTime.Now` vs `UtcNow` matters: prefer `UtcNow` and compare `DateTime` values directly rather than via signed day-diff integers (a wrong-sign value silently passes `<= 7`).

### Patterns this codebase commits to
- **Self-gating predicates over caller-supplied flags.** If a control's "show me" rule can be derived from the item's own data, encode it inside the component, not as a `showX` boolean threaded from every call site. Flag new visibility flags that duplicate a derivable rule.
- **`QueueStatusOrdering` is canonical.** `DisplayOrder` / `SortPriority(status)` in `HurrahTv.Shared.Models` drives Queue tabs, QuickActions, and Home sort, and must match the SQL `CASE` in `DbService.GetQueueAsync`. Flag divergent ad-hoc status ordering.
- **Mutating API endpoints return the updated entity** so the client can splice it in place. Flag mutations that return only a status code when the client needs the new row.
- **Pre-compute per-status/per-tab counts** with `GroupBy().ToDictionary()` after data mutations — never `Count()` per tab inside a render loop.
- **Background/fire-and-forget work** uses `IServiceScopeFactory.CreateAsyncScope()` for fresh transients; don't capture request-scoped services.
- **Cache external calls.** TMDb responses are cached in `TmdbService` (search 30min, trending 1hr, providers 12hr, details 6hr). Every Anthropic call costs money — AI results are cached server-side in `CurationCache` keyed by watchlist hash. Flag new uncached TMDb/Anthropic calls on hot paths.

## Testing expectations

- **Pure `HurrahTv.Shared` logic must have tests** — predicates, filters, sort keys, parsers, extension methods. Bug fixes pin a named regression test referencing the issue number.
- **Blazor components, CSS, page wiring, DI scaffolding do NOT need tests** — they're verified in the browser. Don't ask for unit tests on these surfaces.
- When a feature mixes pure logic with Blazor state, the pure piece should be extracted into `HurrahTv.Shared` and tested there (inject `DateTime todayUtc` as a parameter so tests don't drift across midnight UTC).
- No mocking frameworks, no fluent-assertion libraries — plain `Assert`. Test helpers live inside the test class. Prefer explicit, independent tests over flag-parameterized shared helpers when the cases assert different things.
- `HurrahTv.Api.Tests` uses `WebApplicationFactory<Program>` against real Postgres. Integration-only behavior (concurrency, race paths) that can't be tested deterministically should NOT get a flaky timing-based test.

## Code style

- 4-space indentation. Nullable reference types and implicit usings enabled.
- Prefer `Type variableName` over `var` when the type isn't complex.
- Comments use `//`, start lowercase, and explain *why* when the code isn't self-explanatory. No XML doc comments.
- C# is formatted by `dotnet format` at info severity — CI runs the bare `dotnet format --verify-no-changes --severity info HurrahTv.slnx`, which catches rules (e.g. `IDE0305`, `IDE0330`) that targeted sub-commands miss. Don't nitpick formatting a reviewer can't see; trust the gate.

## DTO / Shared changes

- Adding a property to a Shared DTO is generally safe (default value). Removing or renaming requires Api and Client to change in lockstep in the same change.
- Everything in Shared crosses the wire as JSON — nullable fields, default values, and enum representations all matter. No `ref`/`out` in DTOs. No DI, no platform APIs (`HttpClient`, `IJSRuntime`, `Dapper`, `Npgsql`, ASP.NET/Components types) in Shared.

## Attribution (don't regress)

The UI footer must keep "Data provided by TMDb" and "Watch provider data by JustWatch" with links.
