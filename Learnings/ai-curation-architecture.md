# AI Curation: Two-Phase Architecture

> **Area:** API | AI
> **Date:** 2026-03-29

## Context
Needed AI-powered content curation that picks specific shows for users, explains why, and feels responsive to watchlist changes. Tried genre-only approach first (AI picks genres, TMDb fills blindly) — resulted in duplicate content across rows and no real personalization.

## Learning
The two-phase approach works well:

**Phase 1: Gather candidates** — fetch a broad pool (~80 shows) from TMDb across all the user's streaming services. Use `deep: true` to get 15 results per provider instead of 5. This gives the AI real shows to choose from.

**Phase 2: AI ranks and explains** — send the user's taste profile (Loved > Watched > Watching > WantToWatch, weighted by signal strength) plus the candidate pool to Claude Haiku. AI picks 12-15 shows, orders by recommendation strength, and writes a personalized reason for each.

Key design decisions:
- **Haiku, not Sonnet** — this is structured output (JSON array), not creative writing. Haiku is 5x cheaper and fast enough (~1-2s)
- **Cache by watchlist hash** — SHA256 of `tmdbId:status:rating` for all items. Changes when user adds/removes/rates anything. Only regenerates when hash changes.
- **Never cache empty results** — an empty `[]` from a failed AI call creates a "stuck" state where the hash matches but nothing shows. Always skip caching failures.
- **Cross-row dedup** — if using multiple rows, track `usedAcrossRows` HashSet so shows don't repeat
- **Exclusion must happen at 3 layers:**
  1. **Candidate pool** (`GatherCandidatePoolAsync`) — exclude watchlist + dismissed before AI sees them
  2. **AI prompt** — tell the AI about disliked shows so it avoids similar content
  3. **Safety-net post-filter** (`ExcludeShows` in endpoint) — strip out any shows the user added AFTER the AI cached its results
  Any single layer is insufficient: the candidate pool can be cached stale, the AI can hallucinate, and the user can add shows between cache generation and serving.

## Example
Cost per curation call: ~$0.003-0.005 (Haiku, ~2000 input tokens + 500 output)
Cost per show-match call: ~$0.0006 (Haiku, ~500 input + 50 output)
At 50 show-match views/day = ~$0.03/day per active user
