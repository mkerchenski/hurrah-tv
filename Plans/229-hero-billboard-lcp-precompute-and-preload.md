# Home Hero Billboard LCP — Precompute + Persist + Client Preload — Implementation Plan

> **Status:** Draft
> **Tracking issue:** mkerchenski/hurrah-tv#229
> **Milestone:** v1 Public Launch quality
> **Related plans:** `Plans/home-load-and-wasm-bundle-perf.md` (#175/#3 — do not mix branches), `Plans/home-ai-hero-rotation-and-mobile-drag-fix.md` (#135 — the reservoir/selection architecture this must not regress)
> **Related learnings:** `hero-rotation-reservoir-vs-selection.md`, `ai-curation-architecture.md`, `curation-cache-gotchas.md`, `api-await-with-timeout.md`, `hosted-service-run-owns-dispatch.md`, `date-predicates-prefer-typed-comparisons.md`, `shared-db-slot-swaps-need-backward-compatible-migrations.md`

## Context

The Home hero billboard — the AI-chosen backdrop `<img>` (`HomeHero.razor:15`) — is the **LCP element** and appears too late. It's fetched fire-and-forget after WASM boot via `GET /api/curation/hero`.

**Root cause is discovery-timing, not server latency.** The prior prod trace (`Plans/home-load-and-wasm-bundle-perf.md` Phase 4a, build `d4a2185`) measured: **LCP = 1056 ms, load-delay = 901 ms dominant, image download = 0.6 ms, warm `/hero` endpoint ≈ 38 ms.** The hero URL is unknowable until (1) WASM boots, (2) `OnInitializedAsync` awaits queue + settings, then (3) the `LoadHero()` fetch returns. The hero is **not** cached client-side (`_heroPick` is in-memory only; `ApiClient.GetCuratedHeroAsync` is a bare fetch each load), so every visit rediscovers the URL from scratch, after boot. `preconnect`/`fetchpriority` are already in place and can't help — you can't prioritize a resource the browser can't see in the initial document.

**Intended outcome:** the hero backdrop paints materially faster on a warm load (target ~<1 s), by (a) making `/hero` a cheap, predictable keyed read of a **persisted, precomputed daily hero** (removing TMDb-hydration tail latency from the path), and (b) **preloading the last-known hero image before/parallel to WASM boot** on the client so the LCP image bytes are ready the moment Blazor renders — while preserving the daily rotation + 14-day cooldown exactly.

## Approach at a glance

The warm daily pick is deterministic and stable within a UTC day (`HeroSelector`), so we can **persist the hydrated pick for the day** and serve it as a keyed read, and **cache the same pick client-side** to preload its image before boot. The near-always-identical daily pick makes the pre-boot preload a cache hit for the real LCP image.

---

## Phase 1 — DB: persist the hydrated daily hero *(schema first)* ✅ DONE

**Shipped on branch `feat/229-hero-lcp-precompute-preload`.** `CurationDailyHero` table + `GetDailyHeroAsync`/`SetDailyHeroAsync` in `DbService.cs`; teardown deletes added in `DeleteUserAsync` (also fixed a pre-existing omission — `CurationHeroImpressions` was never deleted on account teardown). 4 new `CurationDailyHeroTests` (round-trip / upsert-overwrite / per-media-type / per-user-scoping + null) — all 70 Api.Tests green, `dotnet format` clean.

**Decision (Npgsql/Dapper):** `ForDate` is a `DATE` column. Npgsql maps bare `date` → `DateOnly` (Dapper can't `Convert.ChangeType` it → `IConvertible` error), and Dapper's param generator can't bind a `DateOnly` param either. So: the public method contract is `DateOnly` (clean, timezone-safe), but internally the SELECT casts `ForDate::timestamp` (→ `DateTime`, wrapped back to `DateOnly`) and the INSERT passes `forDate.ToDateTime(TimeOnly.MinValue)`. **Phase 3's `DailyHeroFreshness` helper should therefore use `DateOnly` (`forDate == today`), not `DateTime` + `.Date`.**



New idempotent table in `DbService.InitializeAsync` (same `CREATE TABLE IF NOT EXISTS` + transaction style as the existing tables, alongside `CurationCache`/`CurationHeroImpressions` at `DbService.cs:87-104`):

```sql
CREATE TABLE IF NOT EXISTS CurationDailyHero (
    UserId        VARCHAR(50) NOT NULL,
    MediaType     VARCHAR(10) NOT NULL,   -- All | tv | movie (hero varies by the Home media filter)
    ForDate       DATE        NOT NULL,   -- UTC calendar day this pick is valid for
    WatchlistHash VARCHAR(64) NOT NULL,   -- ties validity to the reservoir/watchlist, mirrors CurationCache
    HeroJson      TEXT        NOT NULL,   -- serialized hydrated CuratedHero (SearchResult + Reason + Score + providers)
    TmdbId        INT         NOT NULL,
    GeneratedAt   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (UserId, MediaType)
);
```

- One row per `(UserId, MediaType)`; overwritten (upsert `ON CONFLICT (UserId, MediaType) DO UPDATE`) whenever a new day's pick is computed or a shuffle advances it. No backfill (absence = compute on demand). Expand-only migration, safe for slot swaps (`shared-db-slot-swaps-need-backward-compatible-migrations.md`).
- `DbService` methods (mirror `GetCurationCacheAsync`/`SetCurationCacheAsync` at `DbService.cs:792-836`): `GetDailyHeroAsync(userId, mediaType, ct)` → `(string heroJson, DateTime forDate, string watchlistHash, int tmdbId)?`; `SetDailyHeroAsync(userId, mediaType, forDate, watchlistHash, heroJson, tmdbId, ct)`. Add deletion to the account-teardown block (`DbService.cs:968`).
- **Tests:** one `HurrahTv.Api.Tests` round-trip (write → read-back → overwrite) against the real-Postgres fixture.

## Phase 2 — Attribute the hero load *(observability — satisfies AC#1)* ✅ CODE DONE

**Server-Timing shipped** (commit pending). The `/hero` handler (`CurationEndpoints.cs`) now times and appends per-phase metrics: `hero-db` (queue+prefs), `hero-curation` (selection + paid regen; `desc="regen"` when the reservoir regenerated), `hero-tmdb` (hydration). Rides on the existing `ResponseTimingMiddleware` `Server-Timing: app;dur` and is picked up by App Insights (#206) + the #201 RUM beacon — so hero load is now phase-attributed **in the wild**, not just in a one-off trace (AC#1). Phase 3 will add a `hero-daily-hit` entry for the keyed-read path.

- **Baseline:** using the existing 4a prod trace (`d4a2185`: LCP 1056 ms, load-delay 901 ms dominant, image download 0.6 ms, warm `/hero` ≈38 ms) as the "before." A fresh before-trace would cost a second app-run for a number 4a already gives us — so the **after-trace is folded into Phase 5** (one app-run, driven with the owner, proving AC#2 ~<1 s).
- **Tests:** none (observability wiring).

## Phase 3 — Server: serve `/hero` from the persisted daily hero + never block on regen *(Api)*

**Phase 3a (keyed-read) ✅ DONE** (commit pending). `/hero` now: computes the current watchlist hash (`CurationService.ComputeWatchlistHash`, made public), and on a non-shuffle request does a keyed `GetDailyHeroAsync` — if `DailyHeroFreshness.IsFresh` (today + hash match) and the pick isn't now on the user's list (safety-net), returns the stored hydrated `CuratedHero` directly (`hero-daily-hit` Server-Timing), skipping selection *and* TMDb hydration. On miss/shuffle it selects + hydrates as before, then `SetDailyHeroAsync` persists the hydrated pick and records the impression once. New pure `HurrahTv.Shared/Curation/DailyHeroFreshness.cs` (4 tests) + 3 endpoint integration tests (fresh-served / stale-recomputes / shuffle-bypasses, all exercised with AI disabled since the fast path precedes the AI gate). Full solution green (143 Shared + 73 Api + 39 Client), format clean.

**Phase 3b (background reservoir regen) — DEFERRED (decision pending).** Decoupling the paid AI regen from the response addresses the *cold-reservoir* case (new user / 7-day / hash change), which AC#2 explicitly scopes out ("warm load"). It's the highest-risk bit (fire-and-forget AI via `IServiceScopeFactory`, gate/CT ownership). Holding it as an explicit decision rather than bundling it — see original text below.

Original Phase 3 spec (3b portion retained for when/if we build it):

- **`CurationEndpoints.cs` `/hero` handler:** on request, `GetDailyHeroAsync(userId, mediaType, ct)`. Serve it directly (deserialize `HeroJson`) when **`ForDate == DateTime.UtcNow.Date` AND `WatchlistHash == current hash`** (a pure `DailyHeroFreshness` predicate — see below). Apply the safety-net post-filter (`ai-curation-architecture.md`: strip a pick the user just removed/dismissed) before returning.
- **On miss** (new day, hash change, shuffle `refresh=true`, or no row): run the existing selection + hydration path (`CurationService.GetCuratedHeroAsync` → endpoint `ResolveHeroAsync` at `CurationEndpoints.cs:143-155`), then `SetDailyHeroAsync(...)` and `RecordHeroImpressionAsync(...)` **once** (record the impression at compute time, not per read — equivalent behavior, fewer writes). This preserves rotation + 14-day cooldown (`HeroSelector`, AC#3): within-day = keyed read of the same row; next day = row stale → recompute → new pick + impression.
- **Decouple the paid reservoir regen from the response** (`api-await-with-timeout.md`, #129): if the reservoir (`CurationCache`) is stale (7-day/hash) but a prior daily-hero/reservoir pick exists, return the last good pick **immediately** and regenerate the reservoir in the background via `IServiceScopeFactory.CreateAsyncScope()` (`hosted-service-run-owns-dispatch.md`) — never block first paint on the Haiku call. Keep the existing `AiCurationGate` semaphore + budget check. Never cache empty (`curation-cache-gotchas.md`).
- **Shared pure helper + test:** `HurrahTv.Shared/Curation/DailyHeroFreshness.cs` — `IsFresh(DateOnly forDate, string storedHash, string currentHash, DateOnly today)` (`forDate == today && storedHash == currentHash`). `DateOnly` per the Phase 1 decision; inject `today` (`date-predicates-prefer-typed-comparisons.md`).
- **Tests (required — pure Shared logic, named, referencing #229):** `DailyHero_IsStale_At_Utc_Day_Boundary`, `DailyHero_IsStale_On_Watchlist_Hash_Change`, `DailyHero_IsFresh_Same_Day_Same_Hash`. Plus an `Api.Tests` flow test: first call persists + records one impression; same-day second call is a keyed read (no new impression); `refresh=true` advances + overwrites.

## Phase 4 — Client: last-known-hero preload before WASM boot *(Client — the perceived-LCP win)* ✅ CODE DONE (browser-verify pending)

**Shipped in code** (commit pending): new `HeroCache` service (localStorage `hurrah_hero_v1`, camelCase for the JS reader) registered in `Program.cs`; inline pre-boot script in `index.html` (after the TMDb `preconnect`) that reads the cached hero and injects `<link rel="preload" as="image" fetchpriority="high">` for the `w1280` backdrop; `Home.razor` seeds `_heroPick` from `HeroCache` before first paint (clears `_heroLoading` so the cached hero renders immediately, then `LoadHero` fade-swaps the fresh pick) and persists each fresh pick via `HeroCache.SetAsync`. Client builds clean, format clean, 39 Client tests green. **Browser verification is the Phase 5 with-owner step.**

Get the LCP image downloading in parallel with boot.

- **Persist the hero client-side:** after a fresh `/hero` response, write a minimal hero record (backdrop path, tmdbId, mediaType, reason, score) to localStorage keyed by mediaType, plus the last-used mediaType — mirror the existing `UserServicesCache` localStorage pattern. Repurpose/extend rather than add a parallel store.
- **Pre-boot preload script** `HurrahTv.Client/wwwroot/js/hero-preload.js`, registered **early in `index.html`** next to `version.js`/`rum.js` (runs independent of WASM boot). It reads the last-used mediaType's cached hero from localStorage and injects `<link rel="preload" as="image" fetchpriority="high" href="https://image.tmdb.org/t/p/w1280{backdropPath}">` into `<head>`. Pure public-CDN URL — **no auth/token needed**, no API call. `image.tmdb.org` `preconnect` already exists (`index.html:13`).
- **Render the cached hero immediately, fade-swap to fresh:** in `Home.razor` `SelectHero()`/`LoadHero()` (`Home.razor:586-815`), seed `_hero` from the localStorage cache on init so the hero element (and its already-downloading image) paints as soon as boot completes; when `LoadHero` returns the fresh pick, swap it in (no CLS — the skeleton already reserves aspect-ratio height, `Home.razor:331`). First-ever visit (no cache) falls back to today's behavior — documented, one-time.
- **Tests:** none (JS + Razor; browser-verified per CLAUDE.md). Any pure cache key/staleness bit that lands in Shared gets a test.

## Phase 5 — Verify, constants, compound

- Promote any new magic values (daily-hero validity, preload size `w1280`) to named constants near the existing `ReservoirMaxAgeDays`/`DefaultCooldownDays`.
- Re-run the Phase 2 LCP trace on the shipped build; confirm hero load-delay collapses and warm LCP is materially better (~<1 s) — the after-number for AC#2. Confirm rotation/cooldown unregressed (AC#3): same hero within a UTC day, advances next day, 14-day no-repeat, shuffle advances.
- `/compound` the non-obvious learning: *persisting a deterministic within-day pick turns a per-load hydration into a keyed read, and a pre-boot public-CDN image preload is the only lever that beats "URL undiscoverable until boot."*

---

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | **Yes** | `DbService` (`CurationDailyHero` table + get/set/delete), `CurationEndpoints` (`/hero` keyed-read rewire, Server-Timing, background regen dispatch), `CurationService` (background-regen entry / gate reuse). |
| HurrahTv.Client | **Yes** | new `wwwroot/js/hero-preload.js` + `index.html` registration, `Home.razor` (seed `_hero` from cache, fade-swap), `ApiClient`/localStorage hero cache (mirror `UserServicesCache`). |
| HurrahTv.Shared | **Yes** | new pure `Curation/DailyHeroFreshness.cs` (+ tests). No DTO shape change — `CuratedHero`/`CuratedHeroResponse` (`CurationModels.cs:10-22`) are reused as the persisted JSON payload. |

## DB Schema Changes

- New `CurationDailyHero` table, idempotent `CREATE TABLE IF NOT EXISTS` in `DbService.InitializeAsync`. No backfill (absence = compute on demand). Expand-only / backward-compatible for staging↔prod slot swaps. Add teardown delete in the account-deletion block.
- `CurationCache` / `CurationHeroImpressions` unchanged structurally; the daily-hero row's `WatchlistHash` mirrors `CurationCache`'s so validity tracks the reservoir + watchlist together.

## Blazor WASM Considerations

- **Pre-boot JS runs regardless of WASM boot success** (like `rum.js`) — that's the point; keep `hero-preload.js` tiny (bundle weight still matters for first paint).
- **Lifecycle:** hero fetch stays fire-and-forget on `OnInitializedAsync`; guard `StateHasChanged` via `await InvokeAsync(...)` and the existing page-scoped `_cts`/`_disposed` guards (`Home.razor:337,820`). No new disposables.
- Skeleton already reserves height (CLS handled, #175); the cached→fresh fade-swap must not reflow.

## API Considerations

- `/hero` still `RequireAuthorization()` (`CurationEndpoints.cs:14`); still returns the hydrated `CuratedHeroResponse` so the client splices in place.
- Background reservoir regen uses `IServiceScopeFactory.CreateAsyncScope()` for fresh transients (`hosted-service-run-owns-dispatch.md`); bounded by the existing `AiCurationGate` (2) + budget check. Forward `HttpContext.RequestAborted` on the read path (existing #128 pattern); the background regen owns its own CT, not the request's.
- Never cache empty results (`curation-cache-gotchas.md`) — the daily-hero write only happens on a successful hydrated pick.

## External Integrations

- **TMDb:** hydration (`GetDetailsAsync` + `GetWatchProvidersAsync`, cached 6 h/12 h) now runs only on the compute path (first daily load / shuffle), not per warm read — removes its tail-latency from the LCP path. Preload URL hits the public `image.tmdb.org` CDN (no key).
- **Anthropic:** unchanged cost shape — selection is $0; only reservoir regen (≤1 paid Haiku call/user/7 days) costs, and it's now off the first-paint path.
- **Twilio:** untouched.

## Verification

- **Local:** run both apps (`dotnet watch`); sign in; confirm the hero paints from cache immediately on a warm reload, `<link rel="preload">` for the hero image appears in `<head>` at document parse (DevTools Elements/Network, initiator = preload), and the fresh pick swaps in.
- **Rotation/cooldown (AC#3):** same hero within a UTC day across refreshes; pre-seed impressions / advance a day → next-best pick; shuffle (`refresh=true`) advances and overwrites the persisted row; 14-day no-repeat holds.
- **LCP (AC#2):** Chrome DevTools MCP waterfall + Lighthouse before (Phase 2) and after (Phase 5); hero load-delay collapses, warm LCP ~<1 s. Record both on #229 (AC#1 = the phase-attributed capture).
- **Tests:** `dotnet test HurrahTv.slnx` — new `DailyHeroFreshness` Shared tests + `CurationDailyHero` Api.Tests round-trip/flow tests green (existing `HeroSelector` tests still pin rotation).
- **Gate before push:** `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx`; `npm run build:css` if any class/icon changed.

## Follow-on

- **Phase 4 (optional, tracked separately if deferred): background precompute job.** An `IHostedService` (`hosted-service-run-owns-dispatch.md`) that once per UTC day precomputes + persists today's hero for users with an existing reservoir, so even the first daily load is a keyed read. Bounded by TMDb rate limits; scope to recently-active users. Only worth it if Phase 5 shows the first-of-day hydration still hurts — otherwise the lazy compute-on-first-request in Phase 3 already covers it.
- After landing: `/compound` (Phase 5). PR description: `Closes #229`.
- Out of scope: WASM boot time (#3, shipped), client refetch-on-interaction (#175 Phase 4), cross-region App↔DB co-location (#16/#200).
