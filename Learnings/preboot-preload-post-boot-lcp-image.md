# Preloading a Post-Boot-Discovered LCP Image (and Its Two Traps)

> **Area:** WASM | UI | Performance
> **Date:** 2026-07-06
> **Resolves:** mkerchenski/hurrah-tv#229

## Context
The Home hero backdrop is the LCP element, but its URL isn't in the initial HTML — it's the AI-chosen pick, known only after WASM boots *and* `GET /api/curation/hero` returns. A prod trace showed the damage: LCP ≈ 1056 ms with **901 ms of load-delay**, while the image *download* was 0.6 ms and the warm endpoint ≈ 38 ms. The bottleneck was never latency — it was **discoverability**: the browser can't start fetching a resource it can't see until the app boots and an API call resolves.

## Learning
For an LCP image whose URL is only knowable post-boot, `fetchpriority` and `preconnect` **cannot help** — you can't prioritize a resource that isn't in the initial document. The only lever that moves the load-delay is making the URL **discoverable before boot**:

1. Cache the last-shown value (the hero's backdrop path) in `localStorage` whenever a fresh one arrives.
2. In an **inline classic `<script>` in `<head>`** (runs during head parse, before the WASM runtime boots — same slot as the `beforeinstallprompt` capture and `rum.js`), read that cache and inject `<link rel="preload" as="image" fetchpriority="high" href="…">`.

Because the value is stable (here, a daily pick), the preloaded image is almost always the exact one the app renders — so by the time the framework mounts the element, the bytes are already in cache and LCP collapses to ≈ boot + render. Measured result: the hero image was requested at **31 ms** from nav start, served from cache, load-delay effectively gone.

### Trap A — the pre-boot cache key can't be user-scoped, so clear it on sign-out
The pre-boot script runs before any auth code and **can't parse the JWT** to learn which user it is — so the cache key must be a single fixed key, not `hero_<userId>`. That means on a shared device, after User A signs out and User B signs in, B's first paint (and the pre-boot preload) would show A's personalized value. Fix: **clear the key on sign-out** (alongside the auth token). A single fixed key is *required* for the pattern to work pre-boot, so clearing is the only correct mitigation — user-scoping would break the pre-boot read.

### Trap B — seeding render state from the cache must re-apply the same visibility gate the normal path uses
Seeding the component's state from the cache to render immediately (instead of waiting for the fetch) silently bypasses whatever gating the normal render path applies. Here, seeding the hero and clearing the loading flag reintroduced a "flash of the wrong item" that a media-filter + already-on-list gate normally prevents — because the seed cleared the skeleton flag without re-checking those conditions. **Re-apply the exact same predicate the render path applies** (media-filter match, not-already-in-list) before accepting the seed; otherwise the optimization reintroduces the bug the gate was there to stop. Mirror any server-side safety-net (e.g. "don't recommend a title the user already has") on the client seed too.

## Example
```html
<!-- index.html <head>, before the blazor bootstrap script -->
<script>
  (function () {
    try {
      var raw = localStorage.getItem('hurrah_hero_v1');           // single fixed key (Trap A)
      if (!raw) return;
      var path = JSON.parse(raw)?.result?.backdropPath;
      if (!path) return;
      var link = document.createElement('link');
      link.rel = 'preload'; link.as = 'image';
      link.setAttribute('fetchpriority', 'high');
      link.href = 'https://image.tmdb.org/t/p/w1280' + path;      // size must match what the app renders
      document.head.appendChild(link);
    } catch (e) { /* best-effort; never block boot */ }
  })();
</script>
```
```csharp
// Trap A: SignOut clears the non-user-scoped cache alongside the token
await TokenService.ClearTokenAsync();
await HeroCache.ClearAsync();

// Trap B: the seed re-applies the render path's own gate before accepting the cached value
if (cachedHero is { Result: { } seed } && !string.IsNullOrEmpty(seed.BackdropPath)
    && (MediaFilter.MediaType == "all" || seed.MediaType == MediaFilter.MediaType)
    && !_allItems.Any(i => i.TmdbId == seed.TmdbId && i.MediaType == seed.MediaType))
{
    _heroPick = cachedHero; _heroLoading = false; _hero = SelectHero();
}
```

The image size string in the pre-boot script duplicates the render-time size and can't share a C# constant (it's plain JS) — pin it with a comment pointing at the real render call site, since a divergence silently preloads the wrong bytes for zero LCP gain.
