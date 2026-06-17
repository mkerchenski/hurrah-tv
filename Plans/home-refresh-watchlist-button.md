# Home "Refresh Watchlist" Button (safe version) — Implementation Plan

> **Status:** Approved — ready to implement
> **Tracking issue:** mkerchenski/hurrah-tv#211 (created via /xplan on 2026-06-17)

## Context

The Home watchlist reads **stored** per-item episode dates (`LatestEpisodeDate` / `NextEpisodeDate`),
not live TMDb. Those refresh via a fire-and-forget background job on `GET /api/queue`, gated two ways:
a **per-user 12h** staleness gate (`EpisodeCheckStaleAfter`, on each item's `LastEpisodeCheckAt`) and a
**site-wide 6h** TMDb cache (`episode-dates:{showId}` in `IMemoryCache`, shared across users). Because the
refresh is fire-and-forget, its result only appears on the **next** load. Net: a just-aired episode can lag
~12h + a reload before it surfaces on Home (this is exactly what happened with "Baylen Out Loud" — S3E5
aired but the stored dates hadn't been re-checked yet). Documented in `Learnings/api-await-with-timeout.md`.

**Goal:** a Home-page button that force-refreshes the user's active watchlist episode dates *now* and shows
the result immediately — built in the **safe shape** that avoids the rate-limit / cost / cache-stampede
side effects of a naive "bust everything" refresh.

**Safe-shape decisions (the load-bearing ones):**
- **Bypass only the per-user 12h gate** — force-recheck the user's items regardless of `LastEpisodeCheckAt`.
- **Keep the 6h site-wide TMDb cache** (`GetEpisodeDatesAsync` as-is). This is the key safety call: cache
  reuse makes the button near-free, avoids forcing TMDb re-fetches that all users pay for, and prevents a
  cache stampede when a popular show airs. For a "yesterday" episode, ≤6h-old cached data already includes
  it, so bypassing the user gate is sufficient. (Aggressive cache-busting is **explicitly rejected** for v1.)
- **Bounded parallelism + bounded await** so the request can't exhaust the HttpClient pool or hang.
- **Per-user cooldown + client debounce** so the button can't be spammed.
- **Episode dates only** — must NOT trigger AI curation (paid Anthropic calls).

## Affected Projects

| Project          | Touched | Notes                                                                                   |
|------------------|---------|-----------------------------------------------------------------------------------------|
| HurrahTv.Api     | yes     | new `POST /api/queue/refresh-episodes` in `QueueEndpoints.cs`; reuses `TmdbService.GetEpisodeDatesAsync`, `DbService.GetQueueAsync`/`UpdateEpisodeDatesAsync` |
| HurrahTv.Client  | yes     | `ApiClient.RefreshEpisodesAsync`; refresh button + spinner/toast in `Home.razor`        |
| HurrahTv.Shared  | no      | reuses existing `QueueResponse(Items, WatchedEpisodes)` — no DTO change, no ripple       |

**DB schema changes:** none. The cooldown lives in `IMemoryCache` (soft, restart-tolerant); episode writes
reuse the existing `UpdateEpisodeDatesAsync`, which already stamps `LastEpisodeCheckAt`.
**API contract changes:** one new endpoint; returns the existing `QueueResponse` shape so the client reuses
`ProcessQueueResponse`. No change to existing endpoints.

---

## Phase 1 — API: `POST /api/queue/refresh-episodes` (force-refresh, bounded, cooled-down)

Add to the existing `app.MapGroup("/api/queue").RequireAuthorization()` group in `QueueEndpoints.cs`
(so auth is inherited). Handler outline:

1. **Cooldown gate (in-memory, per-user):** key `refresh-cooldown:{userId}` in `IMemoryCache`, TTL ~60s. If
   present, short-circuit: re-read and return the current `QueueResponse` without re-fetching TMDb (the UI
   still gets fresh-from-DB data; we just skip the TMDb work). Set the key at the start of a real refresh.
2. **Select refreshable items:** `db.GetQueueAsync(userId)` → filter to `MediaType == Tv` and
   `Status is Watching or WantToWatch` (mirrors the existing `staleEpisodes` predicate at
   `QueueEndpoints.cs:35`) but **without** the `LastEpisodeCheckAt > 12h` clause — that gate is what we're
   bypassing.
3. **Bounded-parallel force refresh:** for each selected item, `TmdbService.GetEpisodeDatesAsync(item.TmdbId)`
   (keep its 6h cache) → `db.UpdateEpisodeDatesAsync(...)`. Cap concurrency with a `SemaphoreSlim(8)` (or
   chunk by 8) so a large list can't burst TMDb / drain the connection pool. Per-item `try/catch` that logs
   and continues (mirrors `RefreshStaleItemsInBackground`, `QueueEndpoints.cs:182`) so one failure doesn't
   strand the batch (partial-success tolerant).
4. **Bounded await, then re-read** — reuse the `Learnings/api-await-with-timeout.md` pattern exactly:
   `using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5)); await Task.WhenAll(refreshes).WaitAsync(cts.Token);`
   then re-read the queue and return. On `OperationCanceledException` (timeout), return whatever's persisted
   so far — the remaining refreshes complete in the background and land on the next load. Honor the gotchas
   in that learning: `using` CTS (no leaked timer), and inner tasks use
   `catch (Exception ex) when (ex is not OperationCanceledException)` so cancellation isn't misattributed.
5. **Return** `Results.Ok(new QueueResponse(items, watched))` — same shape as `GET /api/queue`.
6. **Scope guard:** episode (and optionally provider) dates ONLY. Do not call any `CurationService` path.

**Reuse:** `TmdbService.GetEpisodeDatesAsync` (`TmdbService.cs:493`, 6h cache + #189 season-scan),
`DbService.GetQueueAsync` (`DbService.cs:230`), `DbService.UpdateEpisodeDatesAsync` (`DbService.cs:544`,
stamps `LastEpisodeCheckAt`), `GetWatchedEpisodesAsync` (`DbService.cs:876`).

**Tests (`HurrahTv.Api.Tests`, WebApplicationFactory + real Postgres):**
- `RefreshEpisodes_Requires_Auth` — 401 without a token.
- `RefreshEpisodes_Within_Cooldown_Skips_Tmdb_And_Returns_Queue` — second call inside the window returns
  current data without re-checking (assert via `LastEpisodeCheckAt` unchanged on the second call).
- If the "refreshable item" predicate is extracted to a pure helper (preferred — it's shared in spirit with
  the `staleEpisodes` filter), unit-test it in `HurrahTv.Shared.Tests` with the status/media matrix.
**Verify:** `dotnet test`; hit the endpoint authenticated and confirm a stale item's dates update and the
response carries the fresh values. **Independently committable** (server-only; button not wired yet).

---

## Phase 2 — Client: ApiClient method + Home refresh button

1. `ApiClient.RefreshEpisodesAsync(CancellationToken)` — `POST /api/queue/refresh-episodes`, returns
   `QueueResponse`. Mirror the existing typed methods in `ApiClient.cs` (e.g. `GetQueueResponseAsync:56`).
2. `Home.razor` — a refresh button in the watchlist section header (the chips/sort control row, ~lines
   178–216). Behavior:
   - `_refreshing` bool gates a spinner on the button and `disabled="@_refreshing"` (client debounce —
     can't double-fire while in flight). Set it before the await, clear in `finally`.
   - On click: `QueueResponse r = await Api.RefreshEpisodesAsync(_cts.Token); ProcessQueueResponse(r);`
     then a toast via the existing `ToastService` — "Watchlist refreshed" (or "Already up to date" on the
     cooldown path; optional). `ProcessQueueResponse` already re-derives rows + re-renders.
   - Respect the page's `_cts` / `_disposed` guards (the hero/queue already use them); wrap the call so a
     navigation-away cancellation is swallowed, not surfaced as an error.
   - Use `InvokeAsync(StateHasChanged)` correctly — the handler is an `async Task` `@onclick`, so a direct
     `StateHasChanged()` after the await is fine (cf. `Learnings/blazor-async-statehaschanged.md`); set
     `_refreshing` before any awaited work so the spinner shows immediately
     (`Learnings/blazor-set-loading-flag-before-derived-state.md`).
**Tests:** none required — Blazor wiring over an existing pure pipeline; browser-verified per CLAUDE.md.
**Verify:** in the browser (visible Ghostty tab), mark a show's stored dates stale, press refresh →
spinner shows, no double-fire on rapid taps, fresh episode surfaces in "Available Now", toast appears;
network panel shows exactly one `POST /api/queue/refresh-episodes` and **no** `/api/curation/*` call.

---

## API Considerations
- New endpoint inherits `RequireAuthorization()` from the `/api/queue` group.
- The force-refresh fan-out runs **inline within the request** (bounded await), not the existing
  fire-and-forget background path — that's the whole point (immediate freshness). The background path on
  `GET /api/queue` stays as-is; both write via `UpdateEpisodeDatesAsync` (last-write-wins, benign).
- Returns the updated `QueueResponse` so the client splices in place (matches the project's
  mutating-endpoint convention).

## External integrations (TMDb)
- TMDb rate limits are real — the **kept 6h cache + bounded concurrency (8) + per-user cooldown** are the
  three guards that keep button presses cheap. Do not remove them together.
- **Anthropic:** the button must never touch curation — keep it out of any `CurationService` call path.

## Follow-on
- After landing: `/compound` if the bounded-await-in-an-explicit-endpoint or cooldown shape surfaces
  anything non-obvious beyond `api-await-with-timeout.md`.
- Possible v2 (out of scope): also refresh provider data (24h) on the same button; a per-show refresh
  affordance on Details/poster; expose "last refreshed" time in the UI.
- Connects to the broader freshness model in `Plans/private/perf-analysis-and-observability.md` (#200) —
  the button is a UX mitigation, not a replacement for the staleness model.
