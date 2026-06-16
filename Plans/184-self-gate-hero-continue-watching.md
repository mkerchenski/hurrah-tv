# Self-gate Home hero "Continue Watching" on media filter (#184) - Implementation Plan

> **Status:** Draft
> **Phase:** N/A (refactor — single phase)
> **Tracking issue:** mkerchenski/hurrah-tv#184

## Context

The Home hero banner is filled by `SelectHero()` in `HurrahTv.Client/Pages/Home.razor`,
which picks from three sources in priority order:

1. **"Recommended for You"** — the rotating AI pick (line 687) — gates inline on `MediaFilter.MediaType`.
2. **"Continue Watching"** — a `Watching` item (lines 708–709) — **does NOT gate inline.**
3. **"New This Week"** — a TV show aired in the last 7 days (line 728) — gates inline (added in #167).

Source 2 reads `_availableNowItems`, which is *already* media-filtered upstream by
`WatchlistFilters.Apply` before `SelectHero` runs, so it does **not** leak the wrong media
type today. But the rule lives upstream, not at the selection site — fragile to any future
refactor that recomputes the hero without re-running the watchlist filter. CLAUDE.md prefers
**self-gating predicates** (rule canonical + grep-able), and this is the same predicate-consistency
theme as `Learnings/predicate-alignment-truth-table.md`: a guard correct at sibling sources 1 and 3
should be present at source 2 so all three answer the media-filter question identically at the
point of decision.

**Intended outcome:** zero behavior change today; the media-filter rule becomes explicit and
consistent across all three hero sources, making new/refactored hero surfaces safe by default.

## Affected Projects

| Project          | Touched | Notes                                                        |
|------------------|---------|--------------------------------------------------------------|
| HurrahTv.Api     | no      | —                                                            |
| HurrahTv.Client  | yes     | `Pages/Home.razor` — one predicate in `SelectHero` source 2 |
| HurrahTv.Shared  | no      | no DTO change; guard stays inline to match sources 1 & 3    |

## Change

In `HurrahTv.Client/Pages/Home.razor`, `SelectHero()` source 2 (`_availableNowItems.FirstOrDefault`,
~line 708), add the media-filter clause already used by source 1 (line 687):

```csharp
QueueItem? watching = _availableNowItems.FirstOrDefault(i =>
    i.Status == QueueStatus.Watching && !string.IsNullOrEmpty(i.BackdropPath)
    && (MediaFilter.MediaType == "all" || i.MediaType == MediaFilter.MediaType));
```

That's the entire functional change. No extraction to Shared — sources 1 and 3 guard inline, so
inline keeps the three consistent and grep-able; a one-off helper would diverge from them.

## Why no test

`SelectHero` is Blazor component logic in `Home.razor`, not pure `HurrahTv.Shared` logic — it
falls under CLAUDE.md's "tests NOT required" surface (Blazor components verified in the browser).
The change is also a no-op on current behavior (the upstream filter already removes off-type items),
so there is no new pure rule to pin. Verify in the browser instead.

## Verification

1. `npm run build:css` in `HurrahTv.Client/` is **not** needed (no class/icon changes).
2. Run the app (API + Client) per CLAUDE.md and load Home with a watchlist containing at least
   one `Watching` TV show **and** one `Watching` movie, both with backdrops, and with AI curation
   either off or resolved (so the hero falls through to source 2).
3. Toggle the Home media filter:
   - **All** → hero may show either (Continue Watching unchanged).
   - **Shows** → Continue Watching hero is the TV show, never the movie.
   - **Movies** → Continue Watching hero is the movie, never the TV show.
4. Confirm no regression to sources 1 (AI pick) and 3 (New This Week) hero selection.
5. `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` before push.

## Follow-on

- After landing, optionally `/compound` is unnecessary here — the self-gating principle is already
  captured in CLAUDE.md and `Learnings/predicate-alignment-truth-table.md`.
