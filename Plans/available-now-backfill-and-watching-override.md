# Available Now: Backfill + Watching Override — Implementation Plan

> **Status:** Complete — shipped via #145 / #170 / #172.
> **Original issue:** #145 (closed) · **Area now refactored by:** #176 (open)
> **Kept as the design record of the current Available Now / Upcoming logic — read this before tackling the #176 refactor.**

## Context

Issue #145 was originally filed for one failure mode: queue items surface in Home "Available Now" with no streaming-service badge. Diagnostic discussion on 2026-05-28 revealed a **second, more painful failure mode** — items don't appear in Available Now *at all* when they should (canonical example: Jimmy Kimmel Live missing daily despite the user actively Watching it on Hulu). The two modes have different root causes and need different fixes, but the user prefers to address them in one plan.

### Failure mode A — no-badge surfacing (original #145)

- `IsStreamableOn` (`QueueItemExtensions.cs:38`) returns `true` for empty providers (intentional post-#141 "don't hide").
- `VisibleServicesFor` (`QueueItemExtensions.cs:51`) needs both user-service membership *and* a `StreamingService.ById` registry hit to render a badge.
- Items pass the visibility gate but render with no badge. Backfill the provider data, render a skeleton while it resolves, suppress items where TMDb genuinely has nothing.

### Failure mode B — items hidden when they should be visible (Jimmy Kimmel)

Two independent gates conspire to hide actively-Watching shows:

1. **`IsLatestEpisodeWatched` + episode-data lag.** User marks last night's Kimmel as watched. The next day TMDb hasn't ingested today's episode yet. `LatestEpisodeDate` still equals the marked-watched episode → `IsLatestEpisodeWatched = true` → hidden all day. Recurring daily for talk shows.
2. **`IsStreamableOn` returns false because TMDb's provider data doesn't list a recognized service.** Confirmed for Kimmel: Details page also shows no Hulu badge, meaning TMDb itself doesn't list Hulu under `results.US` (talk shows live in a gray zone JustWatch handles poorly). `ParseProviders` (`TmdbService.cs:383+`) already reads all five categories (flatrate, ads, free, buy, rent) — the data just isn't there.

The unifying principle for failure mode B:

> **If `Status == Watching`, surface the show. The user committed to it; trust their intent over our gating.**

### Resolved during diagnostic discussion

- **Latency budget for backfill:** 2-second per-request ceiling. Items resolved in time return fresh; items that don't return with `BackfillPending = true` and the client renders skeleton.
- **Branch hygiene:** start from a fresh branch off `main`. PR #141's commits are already in `main` (`f6de30a`, `adb8135`); no overlap.
- **AI-curated rows:** out of scope. `CurationService.cs:176` doesn't gate on streamability at the predicate level (it gates at TMDb candidate-fetch level via `FilterToUserServicesAsync`). AI curation surfaces queue items via `WatchlistRow` and inherits this plan's changes transparently.
- **Add-flow service prompt:** rejected as too much UX friction.
- **Legacy items with no provider data:** left alone; the Watching override + backfill is the fix.
- **No-data badge rendering:** if TMDb genuinely has nothing post-backfill, the row may render with no badge. Honest signal that TMDb doesn't know — the user can still tap into Details.

## What's already there (extend, don't rebuild)

- **DB column:** `QueueItems.AvailableOnCheckedAt TIMESTAMPTZ NULL` (`DbService.cs:146`). Tracks per-item provider freshness.
- **Atomic provider writer:** `DbService.UpdateProvidersAsync` (`DbService.cs:404-419`) writes `AvailableOnJson` + `AvailableOnCheckedAt`.
- **Provider background refresh:** `RefreshStaleItemsInBackground` (`QueueEndpoints.cs:150-201`) runs from `/api/queue` GET, capped at 10 items, fire-and-forget via `IServiceScopeFactory.CreateAsyncScope()`. Follows `Learnings/fire-and-forget-detached-writes.md`.
- **Staleness rule:** `AvailableOnCheckedAt == null` OR older than 24h (`QueueEndpoints.cs:42-43`).
- **TMDb provider fetch:** `TmdbService.GetWatchProvidersAsync` (`TmdbService.cs:334-353`) with 12h IMemoryCache; reads all five TMDb provider categories; normalizes the empty case per `Learnings/tmdb-providers-empty.md`.
- **WatchlistFilters partition:** `HurrahTv.Shared/Filters/WatchlistFilters.cs:19-78` — the canonical Home gate. Already accepts `isStatusActive` and `userServices` and does the multi-row partition in one pass.

**The gaps:**
- The provider refresh is **fire-and-forget**, so the client only sees fresh badges on the next nav. Plan converts the path to **bounded synchronous backfill** (Phase 2).
- There's no **episode-data refresh** mechanism. `LatestEpisodeDate` and `NextEpisodeDate` are populated at add-time and never refreshed for Watching items. Plan adds a parallel mechanism (Phase 3).
- `WatchlistFilters.Apply` doesn't differentiate `Watching` items from other active statuses. Plan adds a permissive override (Phase 1).

## Predicate landscape (decision #3 from issue body)

Per `Learnings/predicate-alignment-truth-table.md`, alignment is verified by a truth table over every input category:

| Predicate | File:line | Question | Empty providers | Registry-gap id user subscribes to |
|---|---|---|---|---|
| `IsStreamableOn(userServices)` | `QueueItemExtensions.cs:34` | streamable on any of my services? | true | true |
| `IsWatchableOn(json, activeIds)` | `QueueEndpoints.cs:206` | server mirror | true | true |
| `VisibleServicesFor(userServices)` | `QueueItemExtensions.cs:51` | which logos can I render? | empty list | excluded (registry gate is correct here) |
| Queue chip filter (inline) | `Queue.razor:301, :517` | demonstrably on **this** service? | false (hides) | n/a |

The Queue chip filter asks a different question (single-service). Plan introduces `IsOnService(int serviceId)` as the canonical single-service helper, semantics matching the post-backfill behavior.

## Phased breakdown

### Phase 1 — Shared changes (HurrahTv.Shared + Tests)

Smallest base changes that ripple to both Api and Client. No DB schema changes here.

- Add `BackfillPending` property on `QueueItem` DTO. Server populates: `true` when the row returned without a fresh provider fetch (exceeded the 2s ceiling). Client reads only.
- Add `IsOnService(this QueueItem item, int serviceId)` to `QueueItemExtensions.cs`. Returns `false` for empty providers (post-backfill semantics). Reuses `ParseAvailableOnProviderIds`.
- Remove the "narrower question" carve-out comment at `QueueItemExtensions.cs:32-33`.
- **Watching override:** modify `WatchlistFilters.Apply` so `Status == Watching` items follow a permissive rule for `IsStreamableOn` and `IsLatestEpisodeWatched`:
  - `IsStreamableOn` is bypassed entirely for `Status == Watching` (user committed → trust them, even if TMDb has no recognized providers).
  - `IsLatestEpisodeWatched` is bypassed when `LatestEpisodeDate` is more than 18 hours old (TMDb episode-data lag → can't trust the latest-known-episode flag).
  - Non-Watching statuses keep all current gates intact (no behavior change for items the user isn't actively watching).
- Tests in `HurrahTv.Shared.Tests/QueueItemExtensionsTests.cs` and a new `WatchlistFiltersTests` class (or extend existing tests):
  - Named regression test pinning #145 — covers both failure modes.
  - Truth-table coverage for `IsOnService` (empty json, `[]`, `[8]` matching, `[999]` registry-gap matching, `[8]` non-matching).
  - Watching-override matrix: Watching + empty providers, Watching + IsLatestEpisodeWatched + stale `LatestEpisodeDate`, Watching + recent `LatestEpisodeDate` (gate respected), non-Watching + same conditions (no override).

**Verify:** `dotnet test HurrahTv.slnx`. Build clean.

### Phase 2 — Bounded synchronous provider backfill (HurrahTv.Api)

- In `QueueEndpoints.cs:17-51` (`/api/queue` GET), convert the fire-and-forget dispatch into a bounded race:
  1. Identify stale items as today.
  2. Kick off per-item refresh tasks.
  3. `await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2), ct)` — race against ceiling.
  4. Items resolved: fresh `AvailableOnJson`, `BackfillPending = false`.
  5. Items not resolved: stale `AvailableOnJson`, `BackfillPending = true`, refresh continues in background (existing fire-and-forget preserved for overflow).
- Keep the existing 10-item batch cap on the synchronous portion.
- Per `Learnings/oce-rethrow-needs-token-filter.md` and `Learnings/api-await-with-timeout.md`, ensure cancellation composes correctly when the request CT fires inside the ceiling.

**Verify:**
- `NULL` out `AvailableOnCheckedAt` for a few dev queue items.
- Load Home; observe `/api/queue` response in DevTools.
- Confirm items within the 2s window return fresh + `BackfillPending = false`; overflow items return stale + `BackfillPending = true`.

### Phase 3 — Episode-data freshness for Watching items (HurrahTv.Api)

Solves the IsLatestEpisodeWatched + TMDb-lag pain by refreshing TMDb episode data for Watching items on every `/api/queue` load. The 18-hour bypass in Phase 1 is the safety net; this phase reduces how often that net is needed.

- Add `EpisodesCheckedAt TIMESTAMPTZ NULL` column to `QueueItems` table via idempotent `ALTER TABLE … IF NOT EXISTS` in `DbService.InitializeAsync`.
- Add `DbService.UpdateEpisodeDataAsync(itemId, latestSeason, latestEpisode, latestEpisodeDate, nextEpisodeDate, checkedAt)` writer — mirror the structure of `UpdateProvidersAsync`.
- In `QueueEndpoints.cs`, identify Watching items with `EpisodesCheckedAt == null || > 6 hours` old.
- Fetch TMDb `tv/{id}` details to get `last_episode_to_air` and `next_episode_to_air`. Reuse `TmdbService.GetShowDetailsAsync` (or add a narrower endpoint if the existing one over-fetches).
- Update the bounded race in Phase 2 to also cover episode refresh — both run in parallel under the same 2s ceiling.
- 6-hour staleness rule chosen because daily shows release new episodes typically late-night; refreshing every 6h catches the new episode within a few hours of TMDb's ingest.

**Verify:**
- Clear `EpisodesCheckedAt` for a Watching item; load `/api/queue`; confirm `LatestEpisodeDate` is current per TMDb.
- For Kimmel-style daily shows, confirm that marking yesterday's episode watched does NOT permanently hide the show — once today's episode is ingested by TMDb, it reappears on next Home load (or via Phase 1's 18-hour bypass if TMDb is still lagging).

### Phase 4 — Skeleton rendering in WatchlistRow (HurrahTv.Client)

- Update `HurrahTv.Client/Components/WatchlistRow.razor` (~line 79) to render a skeleton (pulsing div matching badge size) in the badge slot when `item.BackfillPending == true`.
- Per `Learnings/pre-blanking-state-and-cancellation.md`, ensure the skeleton state doesn't get stuck on page transitions.
- Run `npm run build:css` in `HurrahTv.Client/` if Tailwind utilities for the skeleton aren't already present.

**Verify:**
- Stale queue item → Home → skeleton appears briefly (or persists if backfill exceeded ceiling).
- Reload Home → previously-skeleton item shows real badge.
- Doraemon-style truly-empty item → after backfill, suppressed from Available Now (for non-Watching statuses) or rendered with no badge (for Watching status).

### Phase 5 — Queue per-service chip filter alignment (HurrahTv.Client)

- Replace inline `_itemProviderIds.GetValueOrDefault(i.Id)?.Contains(svcId) == true` at `Queue.razor:301` and `:517` with `i.IsOnService(svcId)`.
- Drop `_itemProviderIds` pre-build at `Queue.razor:481-507` if no other call sites depend on it.
- This makes decision #3 visible: chip filter uses the shared predicate, carve-out is gone, both surfaces give consistent answers.

**Verify:**
- Queue page → select a service chip → items without provider data don't appear; items whose providers match the chip do.
- Toggle chip off → all queue items return.

## End-to-end verification (after all phases)

1. `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` per `Learnings/dotnet-format-ci-runs-bare-not-targeted.md`.
2. `dotnet test HurrahTv.slnx` — including the new regression test pinning #145.
3. Browser smoke:
   - **Mode A:** queue with stale providers → skeleton resolves to real badges.
   - **Mode A:** Doraemon-class show (TMDb genuinely empty) → suppressed from Available Now for non-Watching statuses, rendered without badge for Watching.
   - **Mode B:** Kimmel-style daily show in `Watching` status with last episode marked watched → still appears in Available Now (Watching override).
   - **Mode B:** non-Watching item with `IsLatestEpisodeWatched` → still hidden (gate respected for non-Watching).
4. All AC checkboxes in #145 (post-update) ticked.

## Files to modify

| File | Phase | Change |
|---|---|---|
| `HurrahTv.Shared/Models/QueueItem.cs` | 1 | Add `BackfillPending` property |
| `HurrahTv.Shared/Models/QueueItemExtensions.cs` | 1 | Add `IsOnService`; remove carve-out comment at :32-33 |
| `HurrahTv.Shared/Filters/WatchlistFilters.cs` | 1 | Watching-override branch in `Apply` |
| `HurrahTv.Shared.Tests/QueueItemExtensionsTests.cs` | 1 | `IsOnService` truth-table tests |
| `HurrahTv.Shared.Tests/WatchlistFiltersTests.cs` (new or extend) | 1 | Watching-override matrix + regression test pinning #145 |
| `HurrahTv.Api/Services/DbService.cs` | 3 | `EpisodesCheckedAt` column + `UpdateEpisodeDataAsync` |
| `HurrahTv.Api/Endpoints/QueueEndpoints.cs` | 2, 3 | Bounded race for providers + episodes; set `BackfillPending` |
| `HurrahTv.Client/Components/WatchlistRow.razor` | 4 | Skeleton state in badge slot |
| `HurrahTv.Client/Pages/Queue.razor` | 5 | Replace inline chip filter with `IsOnService`; drop `_itemProviderIds` if unused |

## Existing code to reuse

- `DbService.UpdateProvidersAsync` (`DbService.cs:404-419`) — atomic writer pattern; mirror for `UpdateEpisodeDataAsync` in Phase 3.
- `TmdbService.GetWatchProvidersAsync` (`TmdbService.cs:334-353`) — provider fetch with 12h cache.
- `TmdbService.GetShowDetailsAsync` — episode data source for Phase 3.
- `RefreshStaleItemsInBackground` (`QueueEndpoints.cs:150-201`) — Phase 2 lifts its body into the synchronous race; overflow continues fire-and-forget.
- `QueueItemExtensions.ParseAvailableOnProviderIds` (`QueueItemExtensions.cs:7`) — `IsOnService` helper builds on this.
- `WatchlistFilters.Apply` (`WatchlistFilters.cs:19`) — extend the existing partition rather than introducing a parallel filter.

## Risks

- **Slower `/api/queue` responses** when many items are stale (both providers + episodes). Mitigated by the 2s ceiling and the existing 10-item batch cap. Worth monitoring p95 latency after rollout.
- **TMDb rate limits** — Phase 3 adds an extra TMDb call per Watching item on Home load. Already covered by `TmdbService` caching, but worth watching API usage.
- **Watching override visibility creep.** A user with 50 shows in Watching status could see Available Now grow significantly. Existing partition behavior (collapses lists in the UI) should still apply, but UX review during Phase 4 verify is worthwhile.
- **18-hour bypass false-positives.** A genuinely-finished show (final episode aired weeks ago) marked Watching with `IsLatestEpisodeWatched = true` would now reappear. Mitigation: bypass only triggers when `LatestEpisodeDate > 18h` old AND status is Watching — if both hold, the show probably does have a new episode the user wants to know about. Acceptable trade-off.
- **Predicate-alignment refactor (Phase 5).** Touches the Queue page's primary filter. Truth-table tests in Phase 1 mitigate; manual verification across all chips during Phase 5 verify is essential.

## Hurrah.tv augmentation

### Affected projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | yes | `QueueEndpoints.cs` (Phases 2, 3) — bounded race; `DbService.cs` (Phase 3) — new column + writer |
| HurrahTv.Client | yes | `WatchlistRow.razor` (Phase 4) — skeleton; `Queue.razor` (Phase 5) — chip filter alignment |
| HurrahTv.Shared | yes | `QueueItem` DTO + `QueueItemExtensions` + `WatchlistFilters` — ripples to both sides of the wire |

### DB schema changes

- New column `QueueItems.EpisodesCheckedAt TIMESTAMPTZ NULL` added in Phase 3 via idempotent `ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS EpisodesCheckedAt TIMESTAMPTZ NULL` inside `DbService.InitializeAsync`.
- No backfill needed — `NULL` is the "never checked" sentinel; the freshness rule (Phase 3) treats `NULL` as stale, so all existing items refresh naturally on first Home load.

### Blazor WASM considerations

- **Component lifecycle:** Phase 4's skeleton state inside `WatchlistRow` is component-local — no async subscriptions to dispose. The surrounding partition refresh is driven by `Home.razor`, which already manages its own lifecycle.
- **DI scope:** no new services introduced.
- **Optimistic UI:** the skeleton-to-badge transition is server-driven, not optimistic. After `/api/queue` returns with `BackfillPending = true`, the row stays as a skeleton until the next page load. No client-side polling in this iteration.
- **Skeleton placeholders:** Phase 4 introduces a new Tailwind skeleton — run `npm run build:css` in `HurrahTv.Client/` if utilities like `animate-pulse bg-zinc-700 rounded` aren't already produced by the existing class scan.

### API considerations

- The mutating side of Phases 2 and 3 (provider/episode writes via `UpdateProvidersAsync` / `UpdateEpisodeDataAsync`) follows the existing fire-and-forget background pattern via `IServiceScopeFactory.CreateAsyncScope()` for items that exceed the 2s ceiling. Synchronous path uses the request's existing scope.
- `/api/queue` is already behind `RequireAuthorization()`; no change to authorization.
- Per `Learnings/oce-rethrow-needs-token-filter.md`, the bounded race must distinguish ceiling-cancellation (drop work, mark `BackfillPending`) from request-cancellation (rethrow OCE).

### External integrations

- **TMDb:** Phase 3 adds an extra `tv/{id}` fetch per Watching item per 6h. Existing `TmdbService` IMemoryCache (6h details, 12h providers) absorbs most of the cost. If a Watching item is on multiple users' queues, the cache hit means TMDb sees one fetch per 6h regardless of user count.
- **Anthropic / Twilio:** not touched.

### Follow-on actions

- After landing: invoke `/compound` to capture learnings about the bounded-race pattern (likely worth a learning file like `bounded-synchronous-backfill.md`) and the Watching-override design (when to permit users to override predicate-based gates).
- If the new `EpisodesCheckedAt` column ends up paired with `AvailableOnCheckedAt` semantically, consider whether they should be promoted to a `LastRefreshedAt` JSON column with sub-keys (`{providers: timestamp, episodes: timestamp}`) — defer to a separate refactor.
