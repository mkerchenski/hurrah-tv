# Blazor WASM Custom CSS Isn't Fingerprinted — You Need CI Cache Busting

> **Area:** Deployment | WASM
> **Date:** 2026-04-07

## Context
After every deployment, users had to hard-refresh (Ctrl+Shift+R) to see CSS changes. The browser was serving cached `app.css` and `icons.css` indefinitely.

## Learning
Blazor WASM's `OverrideHtmlAssetPlaceholders` property fingerprints `_framework/` files automatically (the JS bundle gets a hash in its filename). But custom static files in `wwwroot/css/` are NOT fingerprinted — they're served as plain paths with no version hash.

The simplest fix is CI-time injection: `sed` appends `?v={commit-sha}` to CSS `href` attributes in `index.html` before `dotnet publish`. This is zero-maintenance — every deploy gets a unique cache key automatically.

**Pair with proper Cache-Control headers:**
- Files with `?v=` parameter: `public, max-age=31536000, immutable` — cache forever, the hash changes on deploy
- `index.html`: `no-cache` — always revalidate (it's tiny, and it's the file that references everything else)
- Other static files: `public, max-age=3600, must-revalidate` — reasonable default

**Gotcha: `UseStaticFiles` only serves actual files** — bare `/` requests fall through to `MapFallbackToFile("index.html")`, which bypasses the `OnPrepareResponse` callback entirely. The `no-cache` for `index.html` only applies when it's requested as `/index.html` explicitly. For the fallback path, you'd need separate middleware.

**Gotcha: Verify the sed worked** — if `index.html` formatting changes, sed silently does nothing. Add a `grep -q` verification step that fails the build if the injection didn't happen.

## Example
```yaml
# In GitHub Actions workflow, after CSS build, before dotnet publish
- name: Cache-bust CSS references
  run: |
    SHORT_SHA=${GITHUB_SHA::8}
    sed -i "s|href=\"css/app.css\"|href=\"css/app.css?v=$SHORT_SHA\"|" wwwroot/index.html
    sed -i "s|href=\"css/icons.css\"|href=\"css/icons.css?v=$SHORT_SHA\"|" wwwroot/index.html
    grep -q "v=$SHORT_SHA" wwwroot/index.html || { echo "Cache-bust failed"; exit 1; }
```

```csharp
// In Program.cs — tiered cache headers
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Query.ContainsKey("v"))
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        else if (ctx.Context.Request.Path.Value?.EndsWith("index.html") == true)
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        else
            ctx.Context.Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
    }
});
```
