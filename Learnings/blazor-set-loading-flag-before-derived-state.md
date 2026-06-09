# Set a Loading/Skeleton Flag Before Computing the Value It Gates

> **Area:** WASM | UI
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#135

## Context
The Home hero shows a skeleton while the (slow) AI pick loads, then swaps to the pick. The render gate is:

```razor
@if (_hero is not null)   { <HomeHero Item="_hero" /> }
else if (_heroLoading)    { <skeleton /> }
```

`_hero` is produced by `SelectHero()`, called synchronously inside `ProcessQueueResponse()` during init. `SelectHero` returns `null` (so the skeleton shows) *only* when `_heroLoading` is true — otherwise it falls through to a watchlist fallback hero.

## Learning
The flag that gates a derived render value must be set **before** the synchronous code that computes that value — not after kicking off the async work.

The intuitive (wrong) placement sets the flag right before the `await`:

```csharp
ProcessQueueResponse(await queueTask);   // computes _hero via SelectHero() — _heroLoading still false!
_heroLoading = true;                     // too late: _hero is already the fallback, non-null
_ = LoadHero();                          // skeleton branch is now unreachable (_hero != null)
```

`_hero` is computed while `_heroLoading` is still `false`, so `SelectHero` returns the fallback, the `@if (_hero is not null)` branch wins, and the skeleton never renders. The flag assignment is a no-op for the first paint. Correct order:

```csharp
_heroLoading = true;                     // BEFORE the derived-state computation
ProcessQueueResponse(await queueTask);   // SelectHero now returns null → skeleton shows
_ = LoadHero();                          // LoadHero clears _heroLoading in its finally
```

This is easy to get wrong because the natural home for a "start loading" flag is next to the async call that does the loading — but that call runs *after* the synchronous derived-state computation. Both this author and GitHub Copilot independently shipped/flagged the wrong order (it took two passes to actually fix). Whenever a flag suppresses a fallback inside a derived getter, trace where the derived value is first computed and set the flag above that line.

## Update (2026-06-09): recurs in non-lifecycle handlers, and `StateHasChanged()` alone is a no-op
The same trap reappeared in `OnMediaFilterChanged` (a handler subscribed to `MediaFilter.OnChanged`, dispatched via `_ = InvokeAsync(...)`). Two extra wrinkles surfaced:

1. **No automatic render.** `OnInitializedAsync`/`LoadWatchlistData` are lifecycle methods, so Blazor auto-renders at their await points — the derived value (`_hero`) gets recomputed and shown for free. A plain event-subscription handler gets **no** auto-render, so you must trigger it yourself.

2. **`StateHasChanged()` alone doesn't fix it — you must recompute the derived value.** During /xreview, four independent LLM reviewers all proposed "add `StateHasChanged()` after `_heroLoading = true`." That's a no-op for the skeleton: the render gate is `@if (_hero is not null)`, and `_hero` is still the *old* non-null pick at that point (it's only recomputed later, inside `ProcessQueueResponse` after the queue GET). So a bare `StateHasChanged` just re-renders the stale hero. The fix recomputes the gated value with the flag already set:

```csharp
_heroLoading = true;
_hero = SelectHero();   // recompute NOW with the flag set → null (skeleton) or still-valid pick
StateHasChanged();      // non-lifecycle handler: render explicitly
_ = LoadHero();         // refetch in the background
await ReloadWatchlist();
```

Generalization: when a flag gates a *derived* value, "set the flag" means "set the flag **and recompute the derived value**, then render" — not just flip the flag or just call `StateHasChanged`.

## File pointers
- `HurrahTv.Client/Pages/Home.razor` — `OnInitializedAsync` / `LoadWatchlistData` set `_heroLoading` before `ProcessQueueResponse`; `OnMediaFilterChanged` recomputes `_hero` + `StateHasChanged` before the async refetch; `SelectHero` early-returns null while `_heroLoading`
