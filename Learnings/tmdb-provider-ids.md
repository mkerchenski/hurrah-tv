# TMDb Provider IDs Are Unstable

> **Area:** TMDb | Data
> **Date:** 2026-03-28

## Context
Paramount+ content wasn't appearing on the home page despite being selected in user settings. Investigation revealed TMDb had retired provider ID 531 (Paramount+) and replaced it with 2303 (Paramount Plus Premium) and 2616 (Paramount Plus Essential).

## Learning

TMDb provider IDs are not permanent. JustWatch (their data source) periodically reorganizes providers — especially when services rebrand, split tiers, or merge. Provider ID 531 returned only 8 ancient results instead of current Paramount+ content.

**How to verify:** Query `/watch/providers/tv?watch_region=US` and search for the service name. The authoritative list changes over time.

**Current valid US provider IDs (as of 2026-03-28):**

| Service | ID | Notes |
|---------|-----|-------|
| Netflix | 8 | Stable |
| Amazon Prime Video | 9 | Stable |
| Hulu | 15 | Stable |
| Disney+ | 337 | Stable |
| Paramount+ | **2303** | Was 531 — split into Premium (2303) / Essential (2616) |
| Peacock | 386 | Peacock Premium |
| Max | 1899 | Listed as "HBO Max" |
| Apple TV+ | 350 | Listed as "Apple TV" |

**When adding a new service or debugging missing content:** Always verify the provider ID against the live `/watch/providers` endpoint first. Don't trust hardcoded IDs from documentation or old code.

## Example

```bash
# verify provider IDs for US region
curl "https://api.themoviedb.org/3/watch/providers/tv?api_key={key}&watch_region=US" \
  | jq '.results[] | select(.provider_name | test("paramount"; "i"))'
```
