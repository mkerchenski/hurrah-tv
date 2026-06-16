# Issue #39 — Drag-and-Drop Reorder in Queue

> **Status:** Draft
> **Phase:** 1 of 3
> **Branch:** `39-queue-drag-drop-reorder`

## Context

The backend already supports per-user position changes via `PUT /api/queue/{id}/position` (`QueueEndpoints.cs:79`, atomic shift in `DbService.ReorderAsync` lines 254-287), but there is no client UI to drive it. Users today can only express priority by toggling status (Watching / Want to Watch / Watched / Not For Me) — they cannot say "this is the next one I want to watch" within a status.

The user flagged a real concern up front: **the same items render in multiple filtered surfaces, so a single global Position can make ordering feel arbitrary.** That concern is the load-bearing decision in this plan and we settle it in the next two sections before touching any drag library.

---

## Sorting-semantics decision (the core question)

### What `Position` actually does today

`GetQueueAsync` (`DbService.cs:177-194`) sorts the user's queue by **four** keys in order:

1. `CASE Status` — Watching → WantToWatch → Finished → NotForMe
2. `CASE WHEN LatestEpisodeDate >= NOW() - INTERVAL '7 days' THEN 0 ELSE 1 END` — fresh-episode bucket
3. `LatestEpisodeDate DESC` — within the bucket, newer-episode shows float higher
4. `Position` — only as the final tiebreaker

So **`Position` is already a weak signal across the queue as a whole.** It is genuinely effective only when the first three keys all tie. That happens almost exclusively for **WantToWatch** items, which usually have no `LatestEpisodeDate` (the user hasn't started watching), so they collapse into one big tier where Position dominates by default.

For the other tabs:

- **Watching** — sorted by recency of new episodes. Re-arranging by hand would either be visually invisible (user drags item, sort snaps it back the next paint) or would require us to break the recency sort, which is a desirable feature.
- **All** — sorted by Status first; dragging across Status boundaries snaps back to the canonical Status order.
- **Finished / NotForMe** — order is largely cosmetic; users rarely care about priority within "stuff I already watched."

### Recommendation: scope drag affordance to the **Want to Watch** tab only

The acceptance criterion explicitly allows this: *"Works across all queue tabs, OR is scoped to one with a clear reason."* The clear reason is that **Want to Watch is the only tab where Position is the primary sort signal in practice** — every other tab has a stronger algorithmic ordering that would fight the user's drag.

Concretely:

- Drag handle is rendered only when `_activeTab == "wanttowatch"`. On every other tab the handle is absent (not just disabled — absent, so users don't form the wrong mental model).
- No schema changes. Position remains a single global integer per user.
- No change to `GetQueueAsync`'s ORDER BY — the existing 4-key sort already gives the right answer (Position-driven) inside the WantToWatch tier.
- Media-type filter (TV vs Movie) within WantToWatch is **safe** because Position is a strict total order; the dragged item lands in the correct relative slot regardless of which media type is hidden.
- Cross-tab semantics: dragging item A above B in WantToWatch updates Position globally. If both later transition to Watching, A still appears above B as a Position-tiebreaker — the user's intent is preserved silently. This is intuitive: the user expressed "A before B" once, and the system honors it whenever no stronger signal overrides.

**Alternatives considered and rejected:**

- **Per-status Position columns.** Doable but requires a schema change, four parallel Position sequences in `ReorderAsync`, and Status transitions would need to renumber two queues. The benefit is drag works on every tab — but most tabs don't *want* drag overriding their algorithmic sort.
- **Drag works on every tab, swap snapped back by the renderer.** This is the worst option: feels broken, no clear feedback.
- **Drag works on every tab, secondary sort suppressed when user reorders manually.** Adds a hidden "manually-sorted" flag per status — significant complexity for marginal benefit.

---

## Where ordering applies today (audit)

This is the second concern the user raised. Mapping every place queue items render in an ordered list, and what drives their order:

| Surface | File | Ordering source | Affected by drag-reorder? |
|---|---|---|---|
| **Queue page (active tab)** | `Pages/Queue.razor:77-175` | Server `GetQueueAsync` 4-key sort | **Yes** — drag changes `Position` and the WantToWatch tier reflects it on next render |
| Home — Continue Watching | `Pages/Home.razor:220-552`, `SortWatching` | Client sort by `WatchlistSort` user setting (`date` / `sentiment` / `queue`); Position is a tertiary key only in `date` mode | No — recency dominates |
| Home — Upcoming Episodes | same file, `SortUpcoming` | Client sort by `NextEpisodeDate` ascending | No — air-date dominates |
| Home — Want to Watch (movies) | same file, `SortMovies` | Same `WatchlistSort` setting; in `queue` mode Position is secondary after status, in `date` mode tertiary, in `sentiment` mode last | Indirectly — only when user has `WatchlistSort = queue`. We accept this; Position changes propagate correctly when it matters and are silently overridden when the user opted into a different sort. |
| `WatchlistRow.razor` | `Components/WatchlistRow.razor:34-99` | None — renders `Items` parameter as-given | Inherits parent's choice |
| Admin user detail | `DbService.GetAdminUserDetailAsync:843` | `ORDER BY AddedAt DESC` | No — chronological by design |
| `ContentRow` / discover rows | `Components/ContentRow.razor` | TMDb search/discover order, not QueueItems | No — different data source |
| Details page "in your queue" badge | `Pages/Details.razor` | Single-item lookup, no ordering | N/A |

**Net effect of the recommendation:** drag in the Queue page's WantToWatch tab is the only place Position changes are visible *and intentional*. Home page rows continue to honor their existing user-selected sort modes; if a user happens to be on `WatchlistSort = queue`, drag-reorder also reflects there — that's the one bonus surface and it's coherent with the user's mental model.

---

## Plan

### Phase 1 — Drag library + interop scaffolding *(no UI changes yet)*

**Goal:** make a SortableJS interop wrapper that follows the same lifecycle pattern as `hurrahLongPress`, and prove it loads cleanly.

- Add SortableJS via CDN `<script>` in `HurrahTv.Client/wwwroot/index.html` (matches existing pattern — no NPM build step). Pin version.
- Add `hurrahSortable` JS module to `index.html` exposing two functions:
  - `init(listElement, dotNetRef, onReorderMethodName, options)` returns a handle (per `Learnings/blazor-js-interop-handle-lifecycle.md` — must return `IJSObjectReference`, not void).
  - `dispose(handle)` calls `sortable.destroy()`.
  - Options: `{ handle: '.drag-handle', delay: 0, delayOnTouchOnly: true, touchStartThreshold: 5, animation: 150 }` — drag handle restricts the affordance to the explicit grab area (avoids stomping on existing `hurrahLongPress` for QuickActions, which fires on long-press of the card body).
- Verify that touch-drag on iOS does not also fire `hurrahLongPress` — if it does, suppress the long-press timer when touch starts on `.drag-handle` (small JS guard in `hurrahLongPress.js`).

**Files touched:** `wwwroot/index.html` only. No Razor or .cs changes.

**Verify:** open Queue.razor in DevTools, manually call `window.hurrahSortable.init(document.querySelector('table'), null, null, {})` — confirm Sortable instance attaches, drag handle works visually with no .NET wiring yet.

**Commit point.** Independently shippable (pure addition, dormant code).

### Phase 2 — Queue.razor drag UI on Want to Watch tab + optimistic reorder

**Goal:** wire the interop to `Queue.razor`, scoped to `_activeTab == "wanttowatch"`.

- In `Queue.razor`:
  - Add `@implements IAsyncDisposable` and `[Inject] IJSRuntime JS`.
  - Add `_sortableHandle` (`IJSObjectReference?`) and `_dotNetRef` (`DotNetObjectReference<Queue>?`).
  - Add `@key="item.Id"` to the queue row loop (per `Learnings/blazor-key-directive-js-stability.md` — required for any list with JS interop bound to dynamic items, otherwise drag handlers go stale on row removal).
  - Render a drag handle column **only** when `_activeTab == "wanttowatch"`. Use a Heroicons grip icon (`bars-3` or `arrows-up-down`) — needs to land in the icon set, then run `npm run build:css` per CLAUDE.md.
  - In `OnAfterRenderAsync`, when on the WantToWatch tab and the list reference changed: dispose previous handle, init new one. When leaving the tab: dispose handle.
  - In `DisposeAsync`: dispose handle and dotnet ref.
- Add `[JSInvokable] public Task OnReorder(int itemId, int newIndex)`. The `newIndex` is the new position **within the filtered list** the user sees. Translate to the canonical `Position` value: look up `FilteredItems[newIndex - 1]`'s Position (or compute the right neighbor's Position) and call `ApiClient.UpdateQueuePositionAsync(itemId, newPosition)`.
- **Optimistic UI:**
  - Reorder `_items` locally before the await (move the item to the dragged index).
  - `await InvokeAsync(StateHasChanged)` (per `Learnings/blazor-async-statehaschanged.md`).
  - Fire the API call.
  - On non-success: revert `_items` to its pre-drag snapshot, show a toast via the existing toast service, log.
- Suppress the existing `hurrahLongPress` from firing when the touch starts inside `.drag-handle` (one-line guard).

**Files touched:** `Pages/Queue.razor` (markup + code-behind), `wwwroot/index.html` (long-press guard), `tailwind.css` rebuild.

**Verify:**
- Desktop: open Queue → Want to Watch tab → drag a row by the handle → confirm it lands where dropped, no flicker, refresh persists order.
- Desktop on All / Watching / Finished / NotForMe tabs: confirm no drag handle is rendered.
- Mobile (iOS Safari simulator or real device): touch-and-drag the handle (5px threshold), confirm scroll on the rest of the row still works, confirm long-press on the row body still opens QuickActions, confirm long-press on the handle itself does NOT open QuickActions.
- Force a 5xx in DevTools network: confirm row snaps back and a toast surfaces.
- Switch tabs while a reorder is in flight: confirm no exception, no orphaned listener.

**Commit point.** Feature is functionally complete here.

### Phase 3 — Polish + edge cases

- Empty WantToWatch tab: handle absent, no SortableJS init.
- Single item: handle visible but reorder is a no-op (SortableJS handles this; verify).
- Disabled state: while the API call is in flight for a given item, suppress further drags on that row (CSS `pointer-events: none` + visual fade).
- Animation: 150ms transition on row movement, matches existing `transition-colors` cadence.
- Accessibility: drag handle has `aria-label="Reorder {Title}"` and `tabindex="-1"` (keyboard reorder is out of scope for v1; document in Learnings if revisited).
- Add a learning to `Learnings/` capturing the SortableJS-via-interop pattern and the long-press conflict guard.

**Verify:** Run through all acceptance criteria from issue #39 one more time, manually. Confirm no regressions on Home page or other surfaces (which shouldn't see Position changes anyway given the audit above).

---

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | **No** | Existing `PUT /api/queue/{id}/position` endpoint and `DbService.ReorderAsync` are sufficient. |
| HurrahTv.Client | **Yes** | `Pages/Queue.razor`, `wwwroot/index.html` (CDN script + interop module + long-press guard), Tailwind icon rebuild. |
| HurrahTv.Shared | **No** | No DTO changes — `PositionUpdate(int Position)` already exists. |

## DB Schema Changes

**None.** The single global `Position INT NOT NULL DEFAULT 0` column on `QueueItems` is sufficient. Decision rationale lives in the "Sorting-semantics decision" section above.

## Blazor WASM Considerations

- **`@key="item.Id"`** is mandatory on the queue row loop — `Learnings/blazor-key-directive-js-stability.md` documents the exact failure mode (JS handlers go stale after list mutation) we'd otherwise hit when an item is removed mid-session.
- **JS interop handle lifecycle** — `Learnings/blazor-js-interop-handle-lifecycle.md` requires `InvokeAsync<IJSObjectReference>` (not `InvokeVoidAsync`), tracked handle, dispose on tab change and on `DisposeAsync`. Same dictionary-by-id pattern applies if we ever need per-row handles, though for SortableJS we only need one handle per *list*, not per row.
- **`StateHasChanged` after async work** — `Learnings/blazor-async-statehaschanged.md`: optimistic update + revert path must use `await InvokeAsync(StateHasChanged)`, not bare `StateHasChanged()` from a JSInvokable callback.
- **`DotNetObjectReference` lifetime** — created once in `OnAfterRenderAsync` (first time on tab), disposed in `DisposeAsync`. Avoid recreating on every render.
- **No new DI services.** Logic lives in the page's code-behind; if it grows we can extract a `QueueReorderService` (Scoped) in Phase 3.

## API Considerations

- The existing endpoint already returns the right contract (`bool`); no change needed.
- Authorization: endpoint already has `RequireAuthorization()` — drag from an unauth'd state is impossible.
- `ReorderAsync` is transactional and prevents gaps — drag spam will not corrupt the sequence.

## External integrations

None. SortableJS is a self-contained client library loaded via CDN. No TMDb / Anthropic / Twilio surface area touched.

## Follow-on actions

- After Phase 3 lands: invoke `/compound` to capture the SortableJS-interop + long-press-conflict guard pattern in `Learnings/`.
- Consider in a future issue: keyboard-driven reorder (Up/Down arrows on focused handle) for accessibility — explicitly out of scope here.
- Consider in a future issue: bulk reorder by multi-select. Not needed for v1.
