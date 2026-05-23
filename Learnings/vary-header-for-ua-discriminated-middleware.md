# `Vary: User-Agent` Is Mandatory When a Single URL Serves Different Content by UA

> **Area:** API | Deployment | UI
> **Date:** 2026-05-23
> **Resolves:** mkerchenski/hurrah-tv#98

## Context

`OgPreviewMiddleware` returns different responses at the same URL based on the request's User-Agent:

- Bot UA (Twitterbot, facebookexternalhit, Applebot, …) → minimal HTML with per-show `og:*` and `twitter:*` meta tags
- Real browser UA → falls through to `MapFallbackToFile` → WASM bootstrap

The initial implementation set:

```csharp
ctx.Response.ContentType = "text/html; charset=utf-8";
ctx.Response.Headers.CacheControl = "public, max-age=21600"; // 6h
```

…and Copilot's inline review flagged it: a shared cache (App Service edge, CDN, corporate proxy) can pin the **first variant it sees** and serve it to *every* subsequent request at the same URL for up to 6 hours. If the first hit happens to be a bot, real users behind the cache get a bare meta-tag page instead of the app — or vice versa. The variant served depends on whoever hits the URL first after a cache eviction.

## Learning

**A response that varies by request header MUST set `Vary: <header>` if it sets any `Cache-Control` that allows shared caches to store it (`public`, `max-age > 0`, missing `private`).**

This is HTTP/1.1 baseline (RFC 9111 §4.1) but easy to forget in app code because:

1. The local dev server has no shared cache, so testing in the browser always works.
2. `IMemoryCache` (the typical "I added a cache" reflex on the server) is per-process — Vary doesn't affect it because nothing downstream looks at the Cache-Control header.
3. The bug only manifests behind a CDN or shared proxy — Azure App Service, Cloudflare, corp networks. Not visible in a single-user staging test.

The fix is one line:

```csharp
ctx.Response.Headers.Vary = "User-Agent";
```

Concretely for `OgPreviewMiddleware`: the response Vary list must include `User-Agent` because that's the request header the middleware branches on. If we ever add `Accept-Language` branching (e.g. for localized OG cards), append it: `ctx.Response.Headers.Vary = "User-Agent, Accept-Language"`.

## When this applies

Any middleware or endpoint that:

- Inspects a request header
- Returns different `Content-Type` / `body` / `Status` based on what it found
- Sets a `Cache-Control` that allows storage (`public`, `max-age`, anything other than `no-store` / `private`)

Concrete patterns in this repo and others:

- Bot-detect SSR (this case)
- Locale-based responses keyed off `Accept-Language`
- API-versioning via `Accept` header (different JSON shape per version)
- Compression-aware responses keyed off `Accept-Encoding` — though most servers handle this automatically; verify

## When this does *not* apply

- `Cache-Control: no-store` — the response can't be cached anywhere, so Vary is moot.
- `Cache-Control: private, ...` — only the user's own browser may cache, and the browser does include the request in its key. Vary still helps with browser-side determinism but the shared-cache issue isn't present.
- The same URL always returns identical content regardless of headers — Vary is unnecessary noise but harmless.

## Example

`HurrahTv.Api/Middleware/OgPreviewMiddleware.cs`:

```csharp
ctx.Response.ContentType = "text/html; charset=utf-8";
ctx.Response.Headers.CacheControl = "public, max-age=21600"; // 6h
// Vary by UA — same URL serves OG HTML to bots and the WASM bootstrap to real users.
// Without this a shared cache (Cloudflare, App Service CDN) could pin the bot HTML and
// serve it to a Chrome user who'd see a bare meta-tag page instead of the app.
ctx.Response.Headers.Vary = "User-Agent";
await ctx.Response.WriteAsync(RenderOgHtml(details));
```

## Verification

After deploy, curl with and without the bot UA:

```bash
curl -sI -A "Twitterbot/1.0" https://hurrah.tv/details/tv/1399 | grep -iE '(cache-control|vary|content-type)'
curl -sI -A "Mozilla/5.0" https://hurrah.tv/details/tv/1399 | grep -iE '(cache-control|vary|content-type)'
```

The bot response should include `Vary: User-Agent` *and* the right `Content-Type`. The browser response (404 or HTML bootstrap, depending on what the fallback serves) should not include the bot's Cache-Control (because the middleware passes through `next(ctx)` and doesn't set headers in that branch).

If `Vary` is missing on the bot response, a shared cache between you and the server can still serve the wrong variant. Adding `Vary` from the server side is the only fix the app can apply unilaterally.
