# Home AI Hero Rotation + Mobile Drag Fix — Implementation Plan

> **Status:** Draft
> **Tracking issue:** mkerchenski/hurrah-tv#135 + mkerchenski/hurrah-tv#139 (single combined PR; also closes #138 via #135)
> **Scope:** one PR covering two issues — a contained mobile bug fix (#139) and the Home AI presentation rework (#135).

## Context

Two pieces of Home/Queue UX are weak:

1. **#139 — mobile drag-to-reorder is broken.** On the Queue "Want to Watch" tab, dragging a row by its handle works on desktop mouse but on touch the row lifts (gets `sortable-chosen`) yet **no gap opens and it can't be dropped**. Root cause (confirmed): `wwwroot/js/sortable.js` doesn't set `forceFallback`, so SortableJS uses the native HTML5 drag-and-drop API, which **does not fire on touch**. The C# path (`OnReorder` → `MoveItemAsync` → `UpdateQueuePositionAsync`) is correct — this is purely a touch/JS-event problem.

2. **#135 — AI curation feels stale and repetitive.** Four root causes: (a) `CurationCache` only invalidates on watchlist-hash change — `GeneratedAt` exists but is never read, so picks freeze until you touch your list; (b) the candidate pool is recency/popularity-only; (c) the prompt *"STRONGLY prefer 2024-2026"* compounds (b); (d) there is **no selection layer** — the hero is literally `_curatedItems.FirstOrDefault(...)`, always pinned to the AI's #1.

**Decided direction (owner):** replace the flat "Curated for You" grid with a **single rotating AI hero** — the *only* AI surface on Home. The rotation engine separates two concerns: an expensive, periodically-refreshed **reservoir** of scored picks, and a cheap read-time **selection** of one hero per day. Decisions locked:

- **Selection:** deterministic *best-eligible* — highest-scored reservoir item not in the cooldown window.
- **Cooldown:** a title can't return to the hero for **14 days** after being featured.
- **Cadence:** the hero changes **once per calendar day** (server UTC), stable within the day.
- **Reservoir regen (paid AI):** on watchlist change **OR** when the cache is **older than 7 days**.
- **Hero priority:** the rotating AI pick is **primary**; falls back to Continue Watching → New This Week only when curation is empty/unavailable. Resume stays reachable in the "Continue Watching" row below the hero.

---

## Phases

Each phase is independently committable and leaves the system working. Phase 1 (#139) is isolated and ships first to de-risk the PR; Phases 2–6 are the #135 rework, with DB schema as the first #135 phase.

### Phase 1 — #139: fix mobile drag-to-reorder *(Client/JS, isolated)*

- `HurrahTv.Client/wwwroot/js/sortable.js`: add `forceFallback: true` to the merged options so SortableJS uses its own pointer-based drag (works identically on mouse + touch) instead of native DnD. Add `fallbackTolerance: 5` to avoid accidental drags, and keep existing `handle`, `delay: 0`, `delayOnTouchOnly: true`, `touchStartThreshold: 5`. (`fallbackOnBody: true` only if ghost positioning needs it during device testing.)
- `HurrahTv.Client/Pages/Queue.razor`: verify scroll-vs-drag still works with `forceFallback`; the handle already carries `touch-none`. Add `touch-none` to the dragged row **only while a drag is active** if device testing shows the page still scrolls mid-drag (SortableJS toggles `dragClass`; prefer scoping touch-action to that class in CSS over the static row class so normal scroll is unaffected).
- `longpress.js` already bails on `.drag-handle` (line 12) — confirm no regression to the long-press QuickActions sheet.
- **Tests:** none (JS + Razor UI; per CLAUDE.md verify in browser).
- **Files:** `wwwroot/js/sortable.js`, possibly `Pages/Queue.razor` + a CSS rule / `npm run build:css` if a class is added.

### Phase 2 — #135: hero-impression cooldown state *(DB + DbService)*

- New idempotent table in `DbService.InitializeAsync` (same `CREATE TABLE IF NOT EXISTS` + transaction style as the existing tables, ~line 84):
  ```sql
  CREATE TABLE IF NOT EXISTS CurationHeroImpressions (
      UserId  VARCHAR(50) NOT NULL,
      TmdbId  INT         NOT NULL,
      ShownAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      PRIMARY KEY (UserId, TmdbId)
  );
  ```
- `DbService`: `GetHeroImpressionsAsync(userId, ct)` → `Dictionary<int, DateTime>` (TmdbId → last-shown UTC); `RecordHeroImpressionAsync(userId, tmdbId, ct)` → upsert `ShownAt = NOW()` `ON CONFLICT (UserId, TmdbId)`.
- Extend `GetCurationCacheAsync` to **also return `GeneratedAt`** (currently selected columns omit it) so Phase 4 can apply the 7-day check. Tuple becomes `(string? rowsJson, string? watchlistHash, DateTime? generatedAt)?`.
- No behavior change yet.
- **Tests:** one `HurrahTv.Api.Tests` round-trip (record → read-back → cooldown window) — real Postgres fixture.

### Phase 3 — #135: reservoir quality — AI match scores + pool diversification + prompt *(Api/AI)*

- `AICuratedRow` (in `CurationService.cs`): add `Dictionary<int,int> Scores` (TmdbId → 0–100 match strength). **No theme tags** (section removed).
- `CurateWithAIAsync` prompt: request **~25–30 picks** (enough headroom over the 14-day cooldown), each returning `id`, `score` (0–100), `reason`; soften *"STRONGLY prefer 2024-2026"* to allow older highly-rated when the taste connection is strong. Parse `score` into `AIPick`/`Scores`.
- `GatherCandidatePoolAsync`: add a third "deep cut" source alongside New + Popular — an **older highly-rated band** via `discover`, genre-weighted from the user's Favorites/Liked. Requires extending `TmdbService.DiscoverForProviderAsync` (~line 122) with optional `vote_average.gte` / `vote_count.gte` / year-range params (not present today). Keep per-provider interleaving (`provider-popularity-dominance.md`) and flatrate/ads + user-services + watchlist/dismissed exclusion.
- Honor `curation-cache-gotchas.md`: never cache empty; keep the `"[]"` read-skip. Old cache rows lacking `Scores` are treated as needing regen (the 7-day check in Phase 4 will refresh them anyway).
- **Tests:** `HurrahTv.Shared`-eligible pure bits (score parsing) get a small test; pool composition is Api+TMDb (verify by running). The load-bearing pure tests land in Phase 4.

### Phase 4 — #135: rotation engine — selection algorithm *(Shared pure logic + Api wiring)*

- New pure helper `HurrahTv.Shared/Curation/HeroSelector.cs` (factor pure logic into Shared first, per CLAUDE.md testability rule):
  ```csharp
  public record HeroCandidate(int TmdbId, string MediaType, int Score);

  public static class HeroSelector
  {
      // eligible = never shown OR shown today (within-day stability) OR last shown > cooldownDays ago.
      // among eligible, highest Score wins (tiebreak: lowest TmdbId for determinism).
      // fallback when none eligible: least-recently-shown.
      public static HeroCandidate? Select(
          IReadOnlyList<HeroCandidate> reservoir,
          IReadOnlyDictionary<int, DateTime> lastShownUtc,
          DateTime todayUtc,
          int cooldownDays = 14);
  }
  ```
  `todayUtc` injected (no `DateTime.UtcNow` inside) per the testing rule. "Shown today still eligible" is what makes the pick **stable within a calendar day** while still rotating at the UTC midnight boundary.
- Api: refactor curation core to expose the scored reservoir, then add `GetCuratedHeroAsync(userId, watchlist, providerIds, englishOnly, forceRefresh, ct)`:
  1. get reservoir (cached; regen if hash changed **OR** `GeneratedAt` older than 7 days **OR** `forceRefresh`),
  2. load impressions, build `List<HeroCandidate>` from the cached row + `Scores`/`ItemMediaTypes`, run `HeroSelector.Select(..., DateTime.UtcNow)`,
  3. `RecordHeroImpressionAsync` for the chosen pick (idempotent within the day),
  4. hydrate that one pick (reuse the `ResolveRowsAsync` per-item logic at `CurationEndpoints.cs:195` — `GetDetailsAsync` + provider filter).
- New endpoint `GET /api/curation/hero` (`RequireAuthorization`) returning the hero DTO; support force-refresh (query param or keep a `/refresh` that busts the reservoir then re-selects — the issue requires keeping force-refresh). Remove/replace the now-unused `/rows` row-based handler and `CuratedSection` server path; `/match` (show-match) is untouched.
- **Tests (required — pure Shared logic, named, referencing #135):** cooldown exclusion; within-day stability (re-select same `todayUtc` after recording → same hero); daily rotation (advance a day → next-best); no-repeat across a 14-day walk; thin-reservoir fallback (all in cooldown → least-recently-shown); score ordering + tiebreak.

### Phase 5 — #135: Home presentation — hero rotation + remove section + fold #138 *(Shared DTO + Client)*

- `HurrahTv.Shared/Models/CurationModels.cs`: add `CuratedHeroResponse { bool AiEnabled; string? Error; CuratedHero? Hero }` and `CuratedHero { SearchResult Result; string Reason; int Score }`. (Ripples to Api + Client.)
- `ApiClient`: `GetCuratedHeroAsync()` (mirror the existing null-on-error contract); repoint or drop `GetCuratedRowsAsync` / `RefreshCurationAsync`. Repurpose the client `CurationCache` (localStorage, 10-min TTL) to cache the hero response.
- `Home.razor`:
  - Restructure so `SelectHero()` takes the loaded AI pick as input: **AI pick (when loaded) → Continue Watching → New This Week → null**. Render the fallback hero immediately (no blank/CLS), fade-swap to the AI pick when it arrives; skeleton only for fresh users with no fallback hero.
  - Show the one-line AI reason in the hero (`Overview` already carries it).
  - **Remove** the curated section + error block (~lines 243–259), `_curatedItems` / `_aiLoading` / `_aiError` (317–319), `FlattenCuration` (614), the row-based `LoadAICuration`/`RefreshCuration` plumbing (359, 480, 529–612) — replaced by a hero fetch — and the `CurationCache` row usage as needed.
- **Delete** `HurrahTv.Client/Components/CuratedSection.razor` (referenced only by Home.razor).
- `HomeHero.razor` — fold **#138**: the AI hero uses `HeroAction.Add` (primary "Add to list") which is already distinct from "More info" → details. Fix the duplicate for the **fallback Resume** hero (primary "Go to show" and "More info" both navigate to `/details/...`, `OnPrimary`/`OnSecondary` lines 63–81): suppress the secondary "More info" when its destination equals the primary's, so the two CTAs never point at the same URL.
- **Tests:** none (Razor/CSS; verify in browser).

### Phase 6 — polish, learnings, compound

- Promote cooldown (14d), regen (7d), and reservoir size to named constants; sanity-check spend against `AIUsage` after a few days.
- After the prompt change, clear server `CurationCache` (or rely on the 7-day regen + missing-`Scores` regen path).
- `/compound`: capture (a) the reservoir/selection split + the "shown-today-still-eligible" within-day-stability trick, (b) the SortableJS `forceFallback` touch fix. Add learnings.
- Final pass through both issues' acceptance criteria.

---

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | **Yes** | `CurationService` (scores, pool, prompt, `GetCuratedHeroAsync`), `CurationEndpoints` (`/hero`, retire `/rows`), `TmdbService.DiscoverForProviderAsync` (vote/year params), `DbService` (new table + methods, `GeneratedAt` read). |
| HurrahTv.Client | **Yes** | `sortable.js`, `Queue.razor` (#139); `Home.razor`, `HomeHero.razor` (#138), delete `CuratedSection.razor`, `ApiClient`, client `CurationCache`. |
| HurrahTv.Shared | **Yes** | `HeroSelector` pure logic + `HeroCandidate`; `CuratedHeroResponse`/`CuratedHero` DTOs (ripple to both sides). |

## DB Schema Changes

- New `CurationHeroImpressions` table, idempotent `CREATE TABLE IF NOT EXISTS` in `DbService.InitializeAsync`. No backfill needed (absence = never shown = eligible). No bootstrap rows.
- `CurationCache` unchanged structurally; `GeneratedAt` (already present) becomes *read* for the 7-day check.

## Blazor WASM / API / External Considerations

- **Lifecycle:** hero fetch is fire-and-forget on `OnInitializedAsync`; guard `StateHasChanged` with `await InvokeAsync(...)`; nothing new to dispose beyond existing patterns. Skeleton avoids CLS.
- **DTO ripple:** `CuratedHeroResponse` lives in Shared; both Api endpoint and Client deserialize it.
- **API:** `/hero` returns the hydrated hero so the client splices it straight into the hero; `RequireAuthorization`. Reservoir regen is bounded (≤1 paid call/user/7 days worst case); selection + impression recording cost $0 AI.
- **Anthropic:** ~25–30 picks is a few hundred extra Haiku output tokens (negligible); honor the existing `AiCurationGate` + budget check. Never cache empty.
- **TMDb:** the new discover band reuses per-provider interleaving + existing caching; one extra `GetDetailsAsync`/providers call for the single hydrated hero.
- **Time:** server UTC `DateTime.UtcNow` drives cadence; the calendar-day boundary is UTC (acceptable; note in the learning).

## Verification

- **#139:** run both apps (`dotnet watch`), open Queue → Want to Watch on a real iOS Safari + Android Chrome (or device emulation): drag a row by the handle → gap opens, drop reorders, `PUT /api/queue/{id}/position` fires, order survives refresh; desktop mouse drag unregressed; long-press on the row body still opens QuickActions; page doesn't scroll mid-drag.
- **#135:** `dotnet test` (HeroSelector pure tests + the impressions round-trip) green. In-browser: hero shows an AI pick with a reason; `forceRefresh` regenerates; re-loading the page the same day keeps the same hero; simulate a next-day / pre-seeded impression set to confirm rotation and the 14-day no-repeat; confirm fallback to Continue Watching when AI is disabled/empty; confirm the hero's two CTAs never point at the same URL (#138); confirm the old "Curated for You" grid is gone.
- **Gate:** `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` before push; `npm run build:css` if any class/icon changed.

## Follow-on

- `/compound` after landing (Phase 6).
- Out of scope: themed/list-like section (explicitly dropped), keyboard-driven queue reorder, free-text semantic discovery (#105).
