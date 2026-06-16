# Enable PWA — service worker, Android install prompt, manifest polish — Implementation Plan

> **Status:** Draft
> **Phase:** 2 (post-launch enhancement)
> **Tracking issue:** mkerchenski/hurrah-tv#15
> **Branch:** `feat/15-pwa`

## Context

Hurrah.tv already has the *iOS* half of PWA support: a `manifest.webmanifest` (`display: standalone`, theme `#E50914`, 192/512 + maskable icons), `apple-touch-icon`, and a working iOS-Safari install banner (`InstallBanner.razor` + `js/install.js` + `InstallBannerService`). What's missing for #15:

1. **No service worker** — the app can't render its shell offline, and Lighthouse's PWA/installability checks fail without one.
2. **No Android/Chrome install path** — `install.js` only handles iOS Safari (which has no `beforeinstallprompt`). Android/desktop Chrome users get no install affordance even though the browser supports a programmatic prompt.
3. **Manifest gaps** — AC asks for a 256×256 icon; Lighthouse audit unverified.

The intended outcome: installable on iOS *and* Android with a working app-shell cache, **without regressing the existing update flow**. That flow is the key constraint: `UpdateBanner.razor` polls `/api/version` (via `js/version.js`) every 5 min + on navigation and hard-reloads with `Nav.NavigateTo(uri, forceLoad: true)` on a version change. The service worker must let that keep working — so navigations are **network-first**, `/api/*` is **never cached**, and only fingerprinted/static assets are cached.

**Production hosting note:** in prod the WASM client is served *by the Api project* — CI copies `client-output/wwwroot/*` into `api-output/wwwroot/`, and `HurrahTv.Api/Program.cs` serves it via `UseBlazorFrameworkFiles()` + `UseStaticFiles` + `MapFallbackToFile`. So the `service-worker.js` cache-control header lives in the **Api** `Program.cs`, and the SW version-stamp happens in the **Api deploy** CI steps.

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | **yes** | one `else if` in `Program.cs` `OnPrepareResponse` to serve `service-worker.js` as `no-cache` |
| HurrahTv.Client | **yes** | new `wwwroot/service-worker.js`; SW registration in `index.html`; `install.js` Android path; `InstallBanner.razor` Android branch; manifest 256 icon |
| HurrahTv.Shared | no | no DTOs involved |
| CI (`.github/workflows/main_hurrahtv.yml`) | **yes** | stamp `__BUILD_VERSION__` into `service-worker.js` after the wwwroot copy, with `grep -q` verify |

**DB Schema Changes:** none. (So no Phase-1 schema step — the usual "schema first" rule is N/A here.)

## Design decisions (validated)

- **Hand-rolled SW, not the Blazor `service-worker.published.js` + `service-worker-assets.js` integrity manifest.** The integrity-manifest approach gatekeeps app boot on every asset hash matching the manifest — a partial deploy or the CI `sed` rewriting `index.html` after manifest generation → integrity mismatch → app won't boot. We already have content-hash fingerprinting (`OverrideHtmlAssetPlaceholders=true`), CSS `?v=SHA` cache-busting, and a version/update flow, so a minimal network-first SW is strictly safer.
- **Cache name keyed to BuildVersion** (`hurrah-cache-__BUILD_VERSION__`, stamped at deploy). `activate` deletes every cache whose name ≠ current → automatic cleanup, no manifest.
- **`skipWaiting()` + `clients.claim()`** so a new SW takes control immediately (fixes the iOS-standalone "stale index.html" class of bug from `Learnings/mapfallbacktofile-bypasses-static-options.md`). The SW activates **silently** — it does **not** post an update message. `version.js` / `UpdateBanner` remains the sole update prompt, so no double-prompt.
- **Skip SW registration on `localhost`** to keep `dotnet watch` hot-reload clean; local SW testing is done against a published build served through the Api (see Verify).

### Strategy per route class

| Route class | Strategy |
|---|---|
| Navigations / `index.html` / `/` | network-first, offline fallback to cached `index.html` |
| `/api/*` (incl. `/api/version`) | network-only — never cached (watchlist/queue freshness) |
| `_framework/*` (fingerprinted) | cache-first (immutable) |
| `css/*?v=`, icons, `*.webmanifest`, `js/*` | cache-first, populate on first fetch |
| cross-origin (`cdn.jsdelivr.net` SortableJS) | bypass — no `respondWith`, no caching opaque responses |
| `service-worker.js` itself | not intercepted; served `no-cache` by the Api |

---

## Phase 1 — Service worker + offline shell (no update-flow regression)

**Files:** `HurrahTv.Client/wwwroot/service-worker.js` (new), `HurrahTv.Client/wwwroot/index.html`, `HurrahTv.Api/Program.cs`, `.github/workflows/main_hurrahtv.yml`

- [ ] Add `wwwroot/service-worker.js` implementing the per-route strategy above. Cache name `hurrah-cache-__BUILD_VERSION__`. `install` pre-caches the app shell (`/`, `index.html`, manifest, icons); `activate` cleans non-current caches + `clients.claim()`; `fetch` routes by class. Bypass non-GET and cross-origin.
- [ ] Register the SW in `index.html` after the blazor bootstrap script, guarded to skip `localhost`/`127.0.0.1`:
      `if ('serviceWorker' in navigator && !/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) navigator.serviceWorker.register('/service-worker.js');`
- [ ] `Program.cs` (`OnPrepareResponse`, after the `index.html` branch ~line 102): add `else if (path.EndsWith("service-worker.js")) ... CacheControl = "no-cache";`
- [ ] CI: in the **Stamp build version** step (after the wwwroot copy, ~line 71), stamp the SW: `sed -i "s|__BUILD_VERSION__|$SHORT_SHA|" api-output/wwwroot/service-worker.js` + `grep -q "$SHORT_SHA" api-output/wwwroot/service-worker.js || { echo "SW version stamp failed"; exit 1; }`

**Tests:** none (no `HurrahTv.Shared` logic). Verify in browser.

**Verify:**
- Publish locally and serve through the Api: `dotnet publish HurrahTv.Client -c Release -o /tmp/c && cp -r /tmp/c/wwwroot/. HurrahTv.Api/wwwroot/` (or run the Api against a published wwwroot), open in Chrome.
- DevTools → Application → Service Workers: SW registered + activated. DevTools → Network → **Offline** → reload → app shell renders.
- Confirm `/api/*` requests still go to network (not served from cache) and a logged-in watchlist is fresh.
- Confirm the update flow still works: bump `BuildVersion`, redeploy/restart, confirm `UpdateBanner` appears and **Refresh** loads new bytes (no stale shell).

## Phase 2 — Android / Chrome install prompt (`beforeinstallprompt`)

**Files:** `HurrahTv.Client/wwwroot/js/install.js`, `HurrahTv.Client/Components/InstallBanner.razor`, `HurrahTv.Client/Services/BrowserInterop.cs` (extend `InstallBannerService`)

- [ ] `install.js`: add a module-level `beforeinstallprompt` listener that `preventDefault()`s and stashes the event; export `canPromptInstall()` and `promptInstall()` (calls `.prompt()`, awaits `userChoice`, clears the stash). Keep the existing iOS `shouldShow()`/`dismiss()` intact. Listen for `appinstalled` to clear state + set dismissed.
- [ ] `InstallBannerService` (`BrowserInterop.cs`): add `CanPromptInstallAsync()` / `PromptInstallAsync()` wrappers mirroring the existing lazy-import pattern.
- [ ] `InstallBanner.razor`: branch the UI — iOS keeps today's manual "tap Share → Add to Home Screen" copy; when `canPromptInstall()` is true (Android/desktop Chrome), show an **Install** button that calls `PromptInstallAsync()`. Reuse the existing banner styling/dismissal so the surface stays consistent (no separate design pass — this is the same banner, one extra branch). Self-gating per CLAUDE.md: the component decides its own variant from browser capability, no caller flag.

**Tests:** none. Verify in browser.

**Verify:**
- Android Chrome (or desktop Chrome with an installable origin): banner shows **Install** → native prompt → installs.
- iOS Safari: unchanged manual-instructions banner.
- Dismissal persists across reloads (shared `hurrah-install-dismissed` key); `appinstalled` suppresses the banner.

## Phase 3 — Manifest polish + Lighthouse pass

**Files:** `HurrahTv.Client/wwwroot/manifest.webmanifest`, new `wwwroot/icon-256.png`

- [ ] Generate a 256×256 icon from the existing source (`icon.svg` / `icon-512.png`); add to the manifest `icons` array.
- [ ] Run Lighthouse PWA/installability audit against the Release build served through the Api; fix any reds (manifest completeness, icon sizes, `start_url` reachability, SW controlling the page).

**Tests:** none.

**Verify:**
- Lighthouse: installable, no PWA errors.
- Real-device smoke: install on an iPhone (Safari) and an Android device (Chrome); launch from home screen → standalone, no browser chrome, icon correct.

---

## Blazor WASM / hosting considerations

- The SW is plain JS in `wwwroot` — not a Blazor component — so no lifecycle/disposal concerns. The registration is a one-liner in `index.html`.
- `InstallBanner.razor` already wraps JS interop in try/catch in `OnAfterRenderAsync` (per `Learnings/blazor-wasm-async-event-exceptions.md` / `ios-home-screen-pwa.md` gotcha #4) — the new `promptInstall` interop must do the same.
- `InstallBannerService` follows the existing lazy-import + DI-scoped `IAsyncDisposable` pattern in `BrowserInterop.cs`; the new methods slot into that class, no new DI registration needed.

## External integrations

- None touched. The SW explicitly **excludes `/api/*`** so TMDb/Anthropic/Twilio-backed responses are never cached client-side.

## Risks / gotchas

- **iOS purges SW caches** under storage pressure and after ~7 days idle → network-first navigation is essential; never assume `index.html` is cached (Plan-agent finding + `mapfallbacktofile-bypasses-static-options.md`).
- **Don't cache opaque cross-origin responses** (SortableJS CDN, `index.html:53`) — bloats cache, can't be validated.
- **CI sed must run after the wwwroot copy** (the SW lives in client output and is copied into `api-output/wwwroot/`), and must have a `grep -q` guard (the silent-no-op lesson from `blazor-css-cache-busting.md`).
- **`service-worker.js` must be `no-cache`** or the browser pins an old SW; the BuildVersion-keyed cache name is the second line of defense.

## Follow-on actions

- After landing: `/compound` to capture SW-vs-update-flow coexistence as a learning (it's non-obvious and worth recording).
- PR description: `Closes #15`.
