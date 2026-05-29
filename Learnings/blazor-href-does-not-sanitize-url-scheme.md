# Blazor Renders `href="@x"` Without Sanitizing the URL Scheme

> **Area:** WASM | UI
> **Date:** 2026-05-29
> **Resolves:** mkerchenski/hurrah-tv#140

## Context

#140 surfaced a region-level link from TMDb (`results.US.link`) onto `AvailableService.Link`
and bound it straight into a Details-page anchor: `<a href="@svc.Link" target="_blank" …>`.
Three independent reviewers (Copilot + the API/data reviewer + the bug scan) flagged the same
gap: the value goes into the `href` unvalidated.

## Learning

Blazor's renderer **HTML-encodes** attribute values — so `<`, `>`, `"` in a bound string can't
break out of the attribute — but it does **not** validate or block dangerous URL *schemes* in an
`href`. A bound value of `javascript:alert(1)` (or `data:`, `vbscript:`) renders as a live,
clickable link and executes on click. The auto-encoding everyone relies on gives a false sense
of safety here: it stops attribute-injection, not scheme-injection.

This matters whenever you bind a URL you didn't construct yourself — a third-party API value, a
user-supplied link, anything off the wire. TMDb returns `https://` today, so this is
defense-in-depth against a changed/compromised upstream, but the cost of the guard is one line.

**Validate the scheme at the source** (where the value is parsed/assigned), not at each render
site — that way every consumer of the field is safe and new render sites can't forget it.

## Example

In `TmdbService.ParseProviders` — reject anything that isn't `https://` so the chip falls back
to its inert (non-link) form:

```csharp
string link = usData.TryGetProperty("link", out JsonElement lk) ? lk.GetString() ?? "" : "";
// the client renders this straight into an href and Blazor doesn't block javascript:/data: schemes
if (!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) link = "";
```

The Razor side then self-gates on `!string.IsNullOrEmpty(svc.Link)`, so a rejected value renders
a plain `<div>` chip instead of an unsafe `<a>`.
