# Minimal API MapDelete Cannot Infer a Request Body

> **Area:** API
> **Date:** 2026-04-14

## Context
Added a `DELETE /api/episodes/watched` endpoint that accepted a `WatchedEpisodeRequest` body to identify which episode to unmark. The code compiled cleanly with zero warnings, but the API crashed immediately on startup with:

```
System.InvalidOperationException: Body was inferred but the method does not allow inferred body parameters.
```

## Learning
ASP.NET Core Minimal API only performs **automatic body binding** for `POST` and `PUT` verbs. `DELETE`, `GET`, `PATCH`, and `HEAD` do not support inferred body parameters — the framework throws at startup (not at request time), making this a runtime configuration error rather than a per-request failure.

Two valid fixes:
1. **Use route parameters** — `MapDelete("/watched/{tmdbId:int}/{season:int}/{episode:int}", ...)` — cleanest REST convention for DELETE
2. **Add `[FromBody]` explicitly** — `MapDelete("/watched", ([FromBody] WatchedEpisodeRequest req, ...) => ...)` — if you must use a body on DELETE

Option 1 is preferred: HTTP DELETE with a body is technically allowed by spec but widely considered bad practice. Route parameters are self-documenting and avoid the inference restriction entirely.

The failure is silent during build because the constraint is enforced by `RequestDelegateFactory` at startup, not by the compiler. A clean `dotnet build` does not guarantee a clean startup.

## Example
```csharp
// BAD — compiles fine, crashes on startup
group.MapDelete("/watched", async (WatchedEpisodeRequest req, ClaimsPrincipal user, DbService db) => { ... });

// GOOD — route params, no body needed
group.MapDelete("/watched/{tmdbId:int}/{season:int}/{episode:int}",
    async (int tmdbId, int season, int episode, ClaimsPrincipal user, DbService db) => { ... });
```
