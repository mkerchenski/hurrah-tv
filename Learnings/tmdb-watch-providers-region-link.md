# TMDb `watch/providers` Gives a Region-Level `link`, Never a Per-Provider Title URL

> **Area:** TMDb
> **Date:** 2026-05-29
> **Resolves:** mkerchenski/hurrah-tv#140

## Context

#140 wanted "open in app" deep links on the provider badges — tap Netflix, land in Netflix on
that title. The data TMDb returns bounds what's possible.

## Learning

TMDb's `/watch/providers` response nests, per region, a single `link` that sits as a **sibling**
of the `flatrate`/`rent`/`buy` arrays — not inside each provider object:

```jsonc
"results": {
  "US": {
    "link": "https://www.themoviedb.org/tv/1234/watch?locale=US",  // region-level, ONE link
    "flatrate": [ { "provider_id": 8, "provider_name": "Netflix", "logo_path": "/n.png" }, … ]
  }
}
```

That `link` is a **JustWatch landing page for the whole title** (it lists every provider). There
is **no per-provider title URL anywhere in the payload** — TMDb gives you the TMDb id, provider
name/logo, and this one region link. So:

- Every provider badge on a title shares the *same* link. Stamp it onto each `AvailableService`
  during parse; don't try to derive a per-provider URL.
- A chip labelled "Netflix" that opens this link lands on a page listing all providers, **not**
  Netflix-specific. Keep any tooltip/label destination-neutral ("Find where to watch X"), or it
  overpromises a deep link that the data can't back. (Copilot flagged exactly this on #140.)
- **Per-provider deep links and custom schemes (`nflx://`) were rejected for #140** for this
  reason: building any direct provider link needs that provider's *internal* title id (e.g.
  Netflix `80100172`), which TMDb does not provide. The moment you've sourced that id you'd just
  use the provider's plain `https://` universal link — so a custom scheme pays the same
  id-sourcing cost plus scheme upkeep and a no-fallback error dialog, for zero added benefit.

## Takeaway

The JustWatch region `link` is the cheapest, attribution-correct deep link available, and it's
the ceiling unless you separately source per-provider internal title ids (out of TMDb's scope).
