# CLAUDE.md — Hurrah.tv

This file provides guidance to Claude Code when working in this repository.

## What is Hurrah.tv?

A unified streaming queue app — one watchlist across all your streaming services. Search what's available on Netflix, Hulu, Disney+, etc., and manage a single queue.

**Status:** Phases 1–2 complete. Auth, AI curation, sentiment, episode tracking, and Azure deployment are all live.

## Architecture

Blazor WebAssembly frontend + .NET Minimal API backend. Three projects:

| Project | Purpose | Port |
|---------|---------|------|
| **HurrahTv.Api** | Minimal API — TMDb proxy, PostgreSQL database, auth | https://localhost:7201 |
| **HurrahTv.Client** | Blazor WASM — UI, runs in browser | https://localhost:7267 |
| **HurrahTv.Shared** | DTOs shared between API and Client | — |

## Technology Stack

- .NET 10, Blazor WebAssembly (standalone)
- Minimal API (not controllers)
- Dapper + PostgreSQL (migrated from SQL Server)
- Tailwind CSS v4 (CLI build — `npm run build:css` in `HurrahTv.Client/`, run after any class/icon changes)
- TMDb API for catalog data (watch providers sourced from JustWatch)
- Claude Haiku (claude-haiku-4-5-20251001) for AI-powered queue curation via Anthropic SDK
- Twilio for phone OTP SMS delivery
- No Hurrah.Core dependency — this is a standalone product

## Running Locally

Both projects must run simultaneously. Always use `dotnet watch` (not `dotnet run`) for hot reload. Launch each in its own Windows Terminal tab:

```powershell
# API (Terminal 1)
wt new-tab --title "Hurrah.tv API" -d "C:\Users\mkerc\Documents\Hurrah.tv\HurrahTv.Api" pwsh -NoExit -Command "dotnet watch --launch-profile https"

# Client (Terminal 2)
wt new-tab --title "Hurrah.tv Client" -d "C:\Users\mkerc\Documents\Hurrah.tv\HurrahTv.Client" pwsh -NoExit -Command "dotnet watch --launch-profile https"
```

**Ports:** API = https://localhost:7201, Client = https://localhost:7267

**TMDb API key** is in `appsettings.Development.json` (gitignored). The committed `appsettings.json` has a placeholder.

**Shortcut:** `claude-tv` opens a Claude Code session in this directory (red tab, #E50914).

## Key Patterns

### API Endpoints
All endpoints are Minimal API, organized by feature in `Endpoints/` directory:
- `AuthEndpoints.cs` — phone OTP send/verify, JWT issuance (90-day tokens)
- `SearchEndpoints.cs` — TMDb search proxy, trending, discover by provider
- `DetailsEndpoints.cs` — Show/movie details with watch providers
- `QueueEndpoints.cs` — CRUD for the user's watchlist, position reorder, sentiment, progress
- `UserServiceEndpoints.cs` — Streaming services, genre prefs, dismissals, settings
- `SentimentEndpoints.cs` — Per-show, per-season, per-episode sentiment ratings
- `CurationEndpoints.cs` — AI-curated home rows, match scores, usage tracking

### TMDb Integration
- `TmdbService.cs` handles all TMDb API calls with `IMemoryCache`
- Cache durations: search 30min, trending 1hr, providers 12hr, details 6hr
- API key stays server-side — WASM client never touches TMDb directly
- Provider IDs: Netflix=8, Prime=9, Hulu=15, Disney+=337, Paramount+=2303, Peacock=386, Max=1899, Apple TV+=350

### Data Model
PostgreSQL via Dapper (Npgsql). All tables created on startup via `DbService.InitializeAsync()` — no migration files. Tables:
- `Users` — Id, PhoneNumber (UNIQUE), CreatedAt
- `OtpCodes` — PhoneNumber, Code, ExpiresAt, Used
- `QueueItems` — UserId, TmdbId, MediaType, Title, PosterPath, Position, Status, Sentiment, LastSeasonWatched, LastEpisodeWatched, episode date fields
- `UserServices` — UserId, ProviderId (composite PK)
- `UserGenres` — UserId, GenreId (composite PK)
- `UserDismissals` — UserId, TmdbId (composite PK)
- `UserSettings` — UserId (PK), EnglishOnly
- `SeasonSentiments` / `EpisodeSentiments` — per-show granular ratings
- `AIUsage` — token counts and cost tracking per request
- `CurationCache` — cached AI rows keyed by UserId + watchlist hash

### Client Architecture
- Pages call `ApiClient` service (typed HttpClient wrapper — all methods match API endpoints)
- Auth: `HurrahAuthStateProvider` + `TokenService` (JWT in localStorage) + `AuthMessageHandler` (auto-injects Bearer token)
- Key components: `PosterCard`, `PosterGrid`, `ContentRow`, `WatchlistRow`, `QuickActions`, `EpisodeBrowser`, `InstallBanner`, `UpdateBanner`
- UI helpers: `BadgeHelpers.cs` (status colors/icons/labels), `SentimentHelpers.cs` (sentiment colors/icons)
- `BadgeHelpers.AllStatuses` (`IReadOnlyList<QueueStatus>`) is the shared source of truth for status ordering — used in Queue page and QuickActions
- Dark theme, poster-grid layout inspired by Netflix. Mobile bottom tab bar, desktop top nav.
- State lives on the server — client fetches on page load. No client-side state store.
- Prefer **self-gating predicates** over caller-supplied visibility flags. If a control's "show me" rule can be expressed from the item's own data (e.g. `status == Watching && latestEpisodeDate within 7 days`), encode it inside the component rather than passing a `showX` boolean from every call site. Self-gating keeps the rule canonical, makes new surfaces safe by default, and makes the intent grep-able.

## Code Style

- 4-space indentation (see `.editorconfig`)
- Nullable reference types enabled
- Implicit usings enabled
- No XML doc comments — only regular comments (`//`) when code isn't self-explanatory
- Comments start lowercase
- Prefer `Type variableName` over `var` when type isn't complex
- Pre-compute per-status/per-tab counts with `GroupBy().ToDictionary()` after data mutations — never run `Count()` per tab inside a render loop (O(N×tabs) per render)

## Context Management

- Use subagents for research, exploration, and parallel analysis
- Externalize state to files — plans, findings, intermediate results
- One task per subagent for focused execution

## Plans Directory

Design documents and implementation plans stored in `Plans/` at repo root. Excluded from git (local only).

### Plan Format
```markdown
# Feature Name - Implementation Plan

> **Status:** Draft | Active | Complete | On Hold
> **Phase:** 1 | 2 | 3

[Numbered phases with checkable items]
```

## Learnings Directory

Engineering learnings stored in `Learnings/` at repo root. Tracked in git.

### When to Write a Learning
- After discovering something non-obvious about WASM, the TMDb API, or streaming service behavior
- After debugging a subtle issue
- When an architectural decision has tradeoffs worth capturing

## Deployment

- Azure App Service `HurrahTv-Api` with staging + production slots
- Staging auto-deploys on push to `main` (`.github/workflows/main_hurrahtv.yml`)
- Production swap via `/deploy` skill or `swap.yml` workflow (manual trigger)
- Database: Azure Database for PostgreSQL Flexible Server
- CI stamps short SHA as build version into `appsettings.json` + cache-busts CSS with `?v=SHA`
- Domains: hurrah.tv (prod), staging.hurrah.tv (staging)

## Attribution Requirements

TMDb and JustWatch attribution must appear in the UI footer:
- "Data provided by TMDb" with link
- "Watch provider data by JustWatch" with link
