# MapFallbackToFile Bypasses UseStaticFiles Cache Headers

> **Area:** Deployment | WASM
> **Date:** 2026-04-07

## Context
After deploying to production, iOS standalone web app (added to home screen) served stale `index.html` indefinitely. We had `no-cache` set on `UseStaticFiles` for `index.html`, but it wasn't working. Killing and reopening the app was the only way to get updates.

## Learning
`MapFallbackToFile("index.html")` and `UseStaticFiles()` are **completely separate serving pipelines** in ASP.NET Core. The `StaticFileOptions.OnPrepareResponse` callback on `UseStaticFiles` only fires when the static file middleware directly serves a requested file path (e.g., `/index.html`).

SPA routes (`/`, `/search`, `/queue`, `/details/tv/12345`) don't match any static file, so they fall through to `MapFallbackToFile`, which serves `index.html` using its **own default `StaticFileOptions`** — with no cache headers.

This means:
- Direct request to `/index.html` → `UseStaticFiles` → gets `no-cache` ✓
- Request to `/` → `MapFallbackToFile` → default headers (browser caches aggressively) ✗
- Request to `/search` → `MapFallbackToFile` → default headers ✗

iOS standalone mode always loads `/`, so it never hit our cache headers.

**Fix:** Pass a separate `StaticFileOptions` to `MapFallbackToFile`:

## Example
```csharp
// this only applies to direct /index.html requests
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Path.Value?.EndsWith("index.html") == true)
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

// THIS is what serves /, /search, /queue, etc. — needs its own options
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
```
