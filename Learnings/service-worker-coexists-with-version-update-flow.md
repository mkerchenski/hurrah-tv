# Service Worker Must Not Fight the Existing Version/Update Flow

> **Area:** WASM | Deployment
> **Date:** 2026-05-26
> **Resolves:** mkerchenski/hurrah-tv#15

## Context
Adding a PWA service worker to a Blazor WASM app that *already* had a working
update mechanism: `UpdateBanner.razor` polls `/api/version` (via `js/version.js`)
and hard-reloads with `Nav.NavigateTo(uri, forceLoad: true)` when the deployed
version changes. CI also stamps a short SHA as `BuildVersion` and cache-busts CSS
with `?v=SHA`. A naive cache-first SW would have silently broken all of this.

## Learning

A service worker layered onto an app with an existing update flow is a
*coexistence* problem, not a greenfield one. The rules that kept our update flow
working:

1. **Navigations + `appsettings.json` are network-first, not cache-first.**
   `forceLoad: true` issues a real navigation that the SW's `fetch` handler
   *does* intercept (forceLoad bypasses Blazor's router, not the SW). Network-first
   means the reload lands on fresh `index.html` → fresh fingerprinted `_framework/*`
   (cache-miss → refetch). `appsettings.json` must also be network-first because it
   carries `BuildVersion`, which drives the `?v=` cache-bust for lazy JS module
   imports — a stale copy pins old modules.

2. **Hand-roll the SW; do NOT use Blazor's `service-worker.published.js` +
   `service-worker-assets.js` integrity manifest.** That approach gatekeeps app
   *boot* on every asset hash matching the manifest. A partial deploy, or the CI
   `sed` that rewrites `index.html` after the manifest is generated, produces an
   integrity mismatch → app won't boot. We already had content-fingerprinting and
   `?v=` busting, so a minimal network-first SW is strictly safer.

3. **Key the cache name to `BuildVersion`** (`hurrah-cache-<sha>`, stamped in CI
   like the CSS bust). `activate` deletes old caches → automatic per-deploy
   cleanup, no manifest. Scope the cleanup to your own prefix
   (`k.startsWith('hurrah-cache-')`) so you don't wipe CacheStorage owned by other
   features.

4. **SW activates silently** (`skipWaiting()` + `clients.claim()`) and does NOT
   post an "update available" message. `version.js`/`UpdateBanner` stays the sole
   update prompt — otherwise you double-prompt.

5. **Serve `service-worker.js` with `no-cache`, and check that branch BEFORE the
   `?v=` immutable branch** in the Api's `OnPrepareResponse`. Otherwise a future
   fingerprinted SW URL would be pinned `immutable` for a year, defeating updates.

6. **Tie cache writes to `event.waitUntil(...)`.** A fire-and-forget
   `caches.open().then(put)` can be dropped if the SW is terminated between
   microtasks. Pass the `FetchEvent` into the helper and `waitUntil` the put.

7. **`/api/*` is network-only** (return without `respondWith`) — never cache
   per-user mutable data.

## Verifying locally
SW registration is deliberately skipped on `localhost` (so `dotnet watch`
hot-reload isn't intercepted), which also blocks local PWA testing. To verify:
publish Release, assemble the client wwwroot into the Api output (as CI does),
relax the localhost guard *in the throwaway artifact only*, serve over
`http://localhost` (a secure context, so the SW registers), then drive Chrome
DevTools — `emulate` network=Offline + reload proves the shell boots from cache.
`navigator.onLine` stays `true` under CDP offline emulation (known quirk) — judge
offline success by the app rendering, not that flag.

## Example
```js
// fetch routing that preserves the update flow
if (url.pathname.startsWith('/api/')) return;            // network-only
if (req.mode === 'navigate') { respondWith(networkFirst(event, req, OFFLINE_SHELL)); return; }
if (url.pathname === '/appsettings.json') { respondWith(networkFirst(event, req, req)); return; }
respondWith(cacheFirst(event, req));                     // fingerprinted/static
```
```csharp
// Program.cs — SW no-cache MUST precede the ?v= immutable branch
if (path.EndsWith("service-worker.js")) CacheControl = "no-cache";
else if (Query.ContainsKey("v"))        CacheControl = "public, max-age=31536000, immutable";
```
