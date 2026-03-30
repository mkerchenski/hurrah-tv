# CLAUDE.md — Hurrah.tv

This file provides guidance to Claude Code when working in this repository.

## What is Hurrah.tv?

A unified streaming queue app — one watchlist across all your streaming services. Search what's available on Netflix, Hulu, Disney+, etc., and manage a single queue.

**Status:** Early prototype (Phase 1 complete — search, browse, queue)

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
- Tailwind CSS via CDN (v1 — will switch to CLI build for production)
- TMDb API for catalog data (watch providers sourced from JustWatch)
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
- `SearchEndpoints.cs` — TMDb search proxy, trending, discover by provider
- `DetailsEndpoints.cs` — Show/movie details with watch providers
- `QueueEndpoints.cs` — CRUD for the user's watchlist
- `UserServiceEndpoints.cs` — Which streaming services the user subscribes to

### TMDb Integration
- `TmdbService.cs` handles all TMDb API calls with `IMemoryCache`
- Cache durations: search 30min, trending 1hr, providers 12hr, details 6hr
- API key stays server-side — WASM client never touches TMDb directly
- Provider IDs: Netflix=8, Prime=9, Hulu=15, Disney+=337, Paramount+=2303, Peacock=386, Max=1899, Apple TV+=350

### Data Model
PostgreSQL via Dapper (Npgsql). Tables:
- `QueueItems` — UserId, TmdbId, MediaType, Title, PosterPath, Position, Status, AvailableOnJson
- `UserServices` — UserId, ProviderId (composite PK)

### Client Architecture
- Pages call `ApiClient` service (typed HttpClient wrapper)
- Components: `PosterCard`, `PosterGrid`, `ServicePicker`
- Dark theme, poster-grid layout inspired by streaming apps
- State lives on the server (queue, services) — client fetches on page load

## Code Style

- 4-space indentation (see `.editorconfig`)
- Nullable reference types enabled
- Implicit usings enabled
- No XML doc comments — only regular comments (`//`) when code isn't self-explanatory
- Comments start lowercase
- Prefer `Type variableName` over `var` when type isn't complex

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

- Frontend + API: Azure App Service (API serves WASM static files)
- Database: Azure Database for PostgreSQL Flexible Server
- GitHub Actions workflow on push to `main`
- Domain: hurrah.tv

## Attribution Requirements

TMDb and JustWatch attribution must appear in the UI footer:
- "Data provided by TMDb" with link
- "Watch provider data by JustWatch" with link
