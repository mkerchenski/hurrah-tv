# Issue #55 — Service Logos on Posters

> **Status:** Draft
> **Phase:** 3 (Design Polish) / 4 (Queue Management Polish)
> **Related:** d0fcfe0 (24h `AvailableOnJson` refresh), 055d762 (soft-hide UserServices)

## Summary

Show a small streaming-service logo on every meaningfully-sized poster, filtered to the user's active services, so users can see at a glance which of their services a show lives on. The "logo slot" is standardized at bottom-left of the poster. The Queue list view (tiny thumbnails) is exempt and uses inline text+logo in its text column.

## Design (locked before planning — see conversation log)

- **Slot rule:** bottom-left of every full-size poster. Tiny list thumbnails (Queue.razor) render logo inline in the text column instead.
- **PosterCard:** keep bottom gradient; filter `AvailableOn` to user's services; cap at 2 logos; bump to `w-6 h-6 md:w-7 md:h-7`. Same in hover overlay.
- **WatchlistRow:** move title/sentiment/S#E# out of the bottom gradient overlay into a **caption block below the poster** (Netflix "Continue Watching" pattern). Free the gradient to host logos with PosterCard's rules.
- **Queue.razor list:** inline logo + name in the text column, next to TV/Movie and sentiment.
- **Fallback states in PosterCard** (`Not on your services` yellow pill, `Not streaming` gray pill) stay exactly as-is — same slot, just sometimes the "absence" rendering.

## Key Finding From Research

**SearchResult.AvailableOn is already server-filtered** to the user's services on every endpoint feeding PosterCard surfaces (`TmdbService.EnrichUserServicesOnlyAsync` and `FilterToUserServicesAsync`). PosterCard's own client-side filter is therefore *defense-in-depth* — safe and aligns with CLAUDE.md's self-gating principle, but not strictly required.

**QueueItem.AvailableOnJson is NOT user-filtered** — the d0fcfe0 refresh stores all Flatrate/Ads provider IDs. Client-side filter + ID-to-logo lookup is required for WatchlistRow and Queue.razor.

**Curation cache has no regression risk** — `CurationCache` stores TmdbIds only; `ResolveRowsAsync` re-fetches and filters per-request (`CurationEndpoints.cs:195`).

## Phases

### Phase 1 — Foundation: static logo map + UserServicesCache

Shared data and client-side infrastructure needed by every other phase. Nothing visible changes yet.

- [ ] **Populate `LogoPath` in `StreamingService.All`** (`HurrahTv.Shared/Models/StreamingService.cs:11-21`) — add the TMDb logo path for each of the 8 providers (Netflix=8, Prime=9, Hulu=15, Disney+=337, Paramount+=2303, Peacock=386, Max=1899, Apple TV+=350). Use the same path shape TMDb's watch-providers endpoint returns (e.g. `/t2yyOv40HZeVlLjYsCsPHnWLk4W.jpg`). Fetch one real `/watch/providers` response in dev to copy the paths verbatim.
- [ ] **Add `LogoUrl(size)` helper on `StreamingService`** mirroring `AvailableService.LogoUrl(size)` in `SearchResult.cs:55`.
- [ ] **Add `StreamingService.LookupLogoUrl(int providerId, string size)` static** — returns logo URL for a known provider ID or empty string if unknown. This is the single lookup point for any QueueItem-derived surface.
- [ ] **Create `UserServicesCache` scoped service** at `HurrahTv.Client/Services/UserServicesCache.cs`:
  - `Task<IReadOnlyList<int>> GetAsync()` — fetches once, caches, returns.
  - `void Invalidate()` — clears cache.
  - `event Action? Changed` — components subscribe for re-render on settings changes.
  - Uses `ApiClient.GetUserServicesAsync()` (`ApiClient.cs:172`) for the fetch.
- [ ] **Register scoped in `HurrahTv.Client/Program.cs:27`-area** alongside `ApiClient` (same lifetime — tied to auth session).
- [ ] **Hook invalidation in `Settings.razor` `Save()`** (`Pages/Settings.razor:107-123`): after `Api.SetUserServicesAsync()` succeeds, call `UserServicesCache.Invalidate()`.
- [ ] **Pre-load on first authenticated render** — inject into `MainLayout.razor` and `_ = UserServicesCache.GetAsync()` fire-and-forget in OnInit, so the cache is warm before any poster-rendering page tries to use it synchronously-ish.

**Verify:** cache populates once, survives across Home → Search → Queue nav, invalidates when the user toggles a service in Settings.

### Phase 2 — PosterCard refinement

Apply filter + cap + size bump. Used by Home rows, Search (via PosterGrid), any future surface.

- [ ] **Inject `UserServicesCache` into `PosterCard`** (`Components/PosterCard.razor`).
- [ ] **Compute filtered AvailableOn once per render** (C# helper): `AvailableOn.Where(s => userServices.Contains(s.ProviderId)).Take(2)`.
- [ ] **Update bottom strip** (`PosterCard.razor:46,51`): iterate filtered list, change `Take(4)` → `Take(2)`, size `w-5 h-5 md:w-6 md:h-6` → `w-6 h-6 md:w-7 md:h-7`.
- [ ] **Update hover overlay** (`PosterCard.razor:91,96`): same `Take(2)`, keep `w-6 h-6` (hover is desktop-only).
- [ ] **Three-way state ordering stays identical**: filtered-has-items → show logos; `NotOnYourServices` → yellow pill; `NoStreamingInfo` → gray pill. Verify the predicate for "show pill" still works when filtered list is empty but unfiltered list had entries (currently keyed off `Result.AvailableOn.Count > 0` at line 42 — switch to filtered count).
- [ ] **Subscribe to `UserServicesCache.Changed`** to re-render when settings change mid-session. Unsubscribe in `DisposeAsync`.

**Verify:** Home rows, Search results, PosterGrid — all show max 2 logos, both are services the user subscribes to, pills appear correctly for edge cases.

### Phase 3 — WatchlistRow: title caption-below + logo slot

The biggest visual change. Frees the overlay for logos.

- [ ] **Restructure WatchlistRow item block** (`Components/WatchlistRow.razor:35-82`):
  - Wrap the existing card (poster + badges) in a container that also contains a caption `<div>` below it.
  - Remove the bottom gradient title overlay (lines 68-81).
  - New caption block below poster: title + optional sentiment icon on first line, optional S#E# on second line, all `text-xs` / `text-[10px]`, `text-gray-300`, `mt-1.5`.
- [ ] **Add bottom gradient logo slot to WatchlistRow poster**: same gradient classes as PosterCard (`absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/90 to-transparent p-2 pt-6`), same `w-6 h-6 md:w-7 md:h-7` logos.
- [ ] **Compute logos for QueueItem**: deserialize `AvailableOnJson` (`List<int>`), intersect with `UserServicesCache.GetAsync()`, map via `StreamingService.LookupLogoUrl`, take 2.
- [ ] **Inject `UserServicesCache` into `WatchlistRow`**; compute the logo list per item in `@code` (not in markup) — avoids per-render JSON parse in the foreach.
- [ ] **Subscribe to `UserServicesCache.Changed`** like PosterCard.
- [ ] **Flag for visual QA on Home.razor:** caption-below adds ~20-30px per row. Sections are `mb-6 md:mb-8` (`WatchlistRow.razor:5`), should absorb it, but worth eyeballing on the real Home page with "Continue Watching" + "Upcoming" + trending rows stacked.

**Verify:** "Continue Watching" and "Upcoming" rows on Home show title+S#E# below poster, service logo(s) in bottom-left of poster. No vertical rhythm regression.

### Phase 4 — Queue.razor list view: inline logo in text column

Smallest surface, simplest change.

- [ ] **Compute logos per QueueItem** in `Queue.razor` using the same JSON-deserialize + UserServicesCache + LookupLogoUrl path as Phase 3 (factor into a shared helper if it's literally the same — probably `QueueItemExtensions.GetUserServiceLogos(IReadOnlyList<int> userServices)`).
- [ ] **Render inline in the text column** (`Queue.razor:113`-area, the flex row with TV/Movie badge + sentiment): add service logo+name pills next to the existing metadata chips. Style to match the existing `text-[10px] font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded bg-surface-200` pattern — logo first (`w-3.5 h-3.5 rounded`), name after.
- [ ] **If no logos** (user doesn't subscribe to any of this item's providers), render nothing — don't show the "Not on your services" pill here; the list view already hid it via the queue-filter logic in d0fcfe0. Re-confirm that hidden items never reach this rendering path.

**Verify:** Queue list shows service logo+name next to TV/Movie + sentiment chips for each row, scales on mobile without wrapping awkwardly.

### Phase 5 — Shared helper + cleanup

- [ ] **Extract `QueueItemExtensions.GetUserServiceLogos`** (or similar static helper on a suitable shared type) so Phase 3 and Phase 4 both call one implementation.
- [ ] **Remove any dead code** from WatchlistRow's old overlay block (no leftover `<div class="absolute bottom-0 …">` from the removed title gradient).
- [ ] **Tailwind CSS rebuild** — if any new utility classes are used that aren't already in the build, run `npm run build:css` from `HurrahTv.Client/` per CLAUDE.md.
- [ ] **Run the app end-to-end** (both dotnet watch targets) and smoke-test each surface — Home, Search, Queue list, Watchlist rows, hover states on desktop, long-press on mobile, toggle services in Settings and confirm the logos update without a reload.

**Verify:** All four surfaces render correctly and update live when Settings change.

## Data Model Changes

None. `StreamingService.cs` is a Shared model but LogoPath was already declared — we're just populating it.

## API Changes

None. All server-side filtering already in place (`EnrichUserServicesOnlyAsync`, `FilterToUserServicesAsync`, curation `ResolveRowsAsync` provider filter).

## UI Changes

- `Components/PosterCard.razor` — filter + cap + size bump (bottom strip and hover overlay).
- `Components/WatchlistRow.razor` — title/metadata moves below poster; gradient now hosts logos.
- `Pages/Queue.razor` — inline logo+name pill in the text column.
- `Services/UserServicesCache.cs` — new scoped client service.
- `MainLayout.razor` — fire-and-forget cache pre-warm on init.
- `Pages/Settings.razor` — invalidate cache after Save.
- `HurrahTv.Shared/Models/StreamingService.cs` — populate `LogoPath`, add `LogoUrl` + `LookupLogoUrl` helpers.
- `Program.cs` (Client) — register `UserServicesCache` scoped.

## Risks & Open Questions

- **TMDb logo paths could change.** Currently they're stable, but embedding them statically means a TMDb change would show stale logos (or no logos if 404). Mitigation: keep `AvailableService.LogoPath` flowing through SearchResult (it already does via `GetWatchProvidersAsync`) — those stay fresh. Only QueueItem surfaces use the static map, and a broken logo just renders nothing (no crash). Acceptable.
- **`Home.razor` vertical rhythm with caption-below.** Flagged as a visual QA item in Phase 3. If it ends up too tall, fallback options: smaller caption text, or a one-line caption (title + S#E# inline).
- **UserServicesCache pre-warm race.** If a page tries to render before `MainLayout` pre-warm resolves, `GetAsync()` could still be in-flight. Handle with an async-aware pattern: `GetAsync` returns `Task<IReadOnlyList<int>>`, components `await` it in `OnInitializedAsync`, and for render-path access expose a `TryGetCached()` that returns the last-known value (empty list if not yet loaded — poster just renders no logos briefly, same as current behavior before server response). Resolved in Phase 1 implementation.
- **Design underspec:** no related-content strips exist in `Details.razor` today. The issue lists them in scope. Treating this as **out of scope for #55**; if/when a recommendations row is added to the detail page, it will use PosterCard and inherit the Phase 2 changes automatically.

## Testing

Manual smoke per phase (see Verify lines). No automated tests scoped here — to be planned separately via issue #52 (test coverage expansion).

## Rollback

Each phase is independently revertable via `git revert`. Phase 1 is a pure addition (no behavior change). Phases 2–4 touch one component each. Phase 5 is pure cleanup.

## Notes for implementation

- CLAUDE.md: 4-space indent, no XML doc comments, lowercase inline comments only when WHY isn't obvious.
- CLAUDE.md: "Pre-compute per-status/per-tab counts with `GroupBy().ToDictionary()` after data mutations — never run `Count()` per tab inside a render loop" — applies here for the per-QueueItem logo computation in WatchlistRow and Queue.razor (compute once per parameters-set, not per-foreach-iteration per-render).
- CLAUDE.md: `BadgeHelpers.AllStatuses` pattern → similar shared-source-of-truth for the logo lookup belongs on `StreamingService`, not scattered helpers.
