// service-worker.js — minimal hand-rolled SW for offline app-shell resilience.
// Deliberately NOT the Blazor service-worker.published.js + integrity-manifest
// approach: that gatekeeps app boot on every asset hash matching the manifest, and
// a partial deploy (or the CI sed that rewrites index.html) breaks boot entirely.
// We already have content-hash fingerprinting (_framework/*) and ?v=SHA cache-busting,
// so a network-first SW is strictly safer. See Plans/15-pwa.md (#15).
//
// __BUILD_VERSION__ is stamped to the short commit SHA at deploy time (CI), mirroring
// the CSS cache-bust. The cache name is keyed to it so each release gets a fresh
// namespace and activate() drops the old ones. Locally the literal placeholder is the
// cache name (harmless — the SW isn't registered on localhost anyway).

const VERSION = '__BUILD_VERSION__';
const CACHE = `hurrah-cache-${VERSION}`;
const OFFLINE_SHELL = '/index.html';

self.addEventListener('install', (event) => {
    // take over without waiting for old tabs to close — the existing UpdateBanner
    // (/api/version) remains the user-facing update prompt; the SW activates silently.
    self.skipWaiting();
    event.waitUntil(caches.open(CACHE).then((cache) => cache.add(OFFLINE_SHELL)).catch(() => { }));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const req = event.request;

    // let the browser handle non-GET (all API writes, etc.)
    if (req.method !== 'GET') return;

    const url = new URL(req.url);

    // bypass cross-origin (e.g. the SortableJS CDN) — never cache opaque responses
    if (url.origin !== self.location.origin) return;

    // API is per-user and mutable — always network, never cached (watchlist freshness)
    if (url.pathname.startsWith('/api/')) return;

    // network-first for the SPA shell and the runtime config:
    //  - navigations so forceLoad (UpdateBanner refresh) always lands on fresh bytes
    //  - appsettings.json so a deploy's new BuildVersion is picked up (it drives the
    //    ?v= cache-bust for lazy JS module imports — a stale copy would pin old modules)
    if (req.mode === 'navigate') {
        event.respondWith(networkFirstShell(req));
        return;
    }
    if (url.pathname === '/appsettings.json') {
        event.respondWith(networkFirst(req));
        return;
    }

    // everything else same-origin (_framework/*, css, js, icons, manifest) is
    // fingerprinted or ?v=-busted, so cache-first is safe and self-correcting.
    event.respondWith(cacheFirst(req));
});

async function networkFirstShell(req) {
    try {
        const res = await fetch(req);
        if (res && res.ok) {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(OFFLINE_SHELL, copy));
        }
        return res;
    } catch {
        const cached = await caches.match(OFFLINE_SHELL);
        return cached || Response.error();
    }
}

async function networkFirst(req) {
    try {
        const res = await fetch(req);
        if (res && res.ok) {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(req, copy));
        }
        return res;
    } catch {
        const cached = await caches.match(req);
        return cached || Response.error();
    }
}

async function cacheFirst(req) {
    const cached = await caches.match(req);
    if (cached) return cached;
    try {
        const res = await fetch(req);
        // only cache successful same-origin responses
        if (res && res.ok && res.type === 'basic') {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(req, copy));
        }
        return res;
    } catch {
        return Response.error();
    }
}
