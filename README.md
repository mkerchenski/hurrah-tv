# Hurrah.tv

AI-curated streaming across all your services. One smart watchlist that learns what you love and surfaces what to watch next.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![Blazor WASM](https://img.shields.io/badge/Blazor-WebAssembly-512BD4) ![PostgreSQL](https://img.shields.io/badge/PostgreSQL-4169E1) ![Claude AI](https://img.shields.io/badge/Claude_AI-Anthropic-D4A574)

## What it does

Hurrah.tv is an opinionated streaming platform that curates content across Netflix, Hulu, Disney+, Prime Video, Max, Peacock, Paramount+, and Apple TV+. It learns from your watch history and preferences to build a personalized home feed — like opening one app instead of eight.

- **AI-powered curation** — Claude analyzes your taste (and addresses you by name) to generate themed content rows ("High-Stakes Hospital Nights", "Slow-Burn Thrillers That Hook Fast")
- **Hero billboard** — Top of Home picks from your Continue Watching, latest aired this week, or top AI rec — whichever fits, with a Netflix-style backdrop and Resume / Watch / Add CTA
- **Smart watchlists** — Track what you're watching, want to watch, and have watched. Separate sentiment system (thumbs up/down/favorite) lets you rate shows, seasons, and individual episodes independently from your list.
- **Optimistic UI** — Marking watched, changing status, or rating closes the modal instantly and updates the watchlist row in place; the API call runs in the background
- **New episode alerts** — Shows in your list with new or upcoming episodes are flagged automatically with "returning" indicators for shows you've finished
- **Multi-step onboarding** — Pick a name, services, genres, then at least 3 shows with sentiment so AI works from day one
- **Balanced discovery** — Content is interleaved across your services so no single provider dominates your feed
- **Language filter** — Option to show English originals only, hiding dubbed and subtitled content
- **Admin views** — Owner-only `/admin` surface for users (with name + admin grant), AI usage and budget tracking, and onboarding funnel

**Live at:** [hurrah.tv](https://hurrah.tv)

## Architecture

Three-project solution: Blazor WebAssembly frontend + .NET Minimal API backend + shared DTOs.

```
HurrahTv.Api/        .NET Minimal API — TMDb proxy, Claude AI curation, PostgreSQL
HurrahTv.Client/     Blazor WASM — runs entirely in the browser
HurrahTv.Shared/     DTOs shared between API and Client
```

### Home Page Feed

The home page renders Netflix-style horizontal content rows, each powered by a different data source:

| Row type | Source | Example |
|----------|--------|---------|
| Hero Billboard | Continue Watching / latest aired this week / top AI pick (self-gating) | Full-bleed backdrop with Resume / Watch latest / Add to list |
| Continue Watching | User's "Watching" list + air dates | Most recently aired first with "Xd ago" badges |
| Upcoming Episodes | All non-dismissed shows + TMDb air dates | Next 7 days, with "Returning" flag for finished shows |
| AI-Curated | Claude AI + TMDb discover | "High-Stakes Hospital Nights" |
| New This Season | TMDb discover (date-filtered) | Recently aired TV across your services |
| Trending TV Shows | TMDb popularity + recency boost | Popular TV, newer ones first |
| New Releases | TMDb discover (date-filtered) | Recently released movies |
| Trending Movies | TMDb popularity + recency boost | Popular movies, newer ones first |
| Because You Loved X | TMDb recommendations API | Similar shows to your favorites |

### API Endpoints

| Endpoint group | Purpose |
|----------------|---------|
| `/api/search` | TMDb search proxy, trending, discover, recommendations |
| `/api/details` | Show/movie details with watch providers and episode info |
| `/api/queue` | Watchlist CRUD — statuses, sentiment, progress, "seen it" |
| `/api/curation` | AI-curated picks, per-show match scoring, usage tracking |
| `/api/services` | Manage subscribed streaming services |
| `/api/shows` | Season and episode sentiment CRUD |
| `/api/settings` | User preferences (English-only filter, etc.) |
| `/api/auth` | Phone OTP login (SMS-based, no passwords) |
| `/api/profile` | First-name capture for greetings + AI personalization |
| `/api/admin` | Owner-only — users list, per-user detail, AI usage rollups, onboarding funnel, hard-delete |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for Tailwind CSS build)
- PostgreSQL 17+ (`brew install postgresql@17` on Mac, `winget install PostgreSQL.PostgreSQL` on Windows)
- A [TMDb API key](https://www.themoviedb.org/settings/api) (free)
- An [Anthropic API key](https://console.anthropic.com/) (for AI curation — optional, app works without it)

### Setup

1. Clone and install:
   ```bash
   git clone https://github.com/mkerchenski/hurrah-tv.git
   cd hurrah-tv
   cd HurrahTv.Client && npm install && npm run build:css && cd ..
   ```

2. Configure `HurrahTv.Api/appsettings.Development.json` with your keys:
   ```json
   {
     "ConnectionStrings": { "Default": "Host=localhost;Database=HurrahTv;Username=postgres;Password=your-password" },
     "Tmdb": { "ApiKey": "your-tmdb-api-key" },
     "Jwt": { "Key": "your-base64-jwt-key" },
     "Twilio": { "AccountSid": "your-sid", "AuthToken": "your-token", "FromNumber": "+1234567890" },
     "AI": { "Enabled": true, "AnthropicApiKey": "your-anthropic-api-key" }
   }
   ```

3. Run both projects simultaneously:
   ```bash
   # Terminal 1 — API
   cd HurrahTv.Api && dotnet watch --launch-profile https

   # Terminal 2 — Client
   cd HurrahTv.Client && dotnet watch --launch-profile https
   ```

4. Open https://localhost:7267

## Tech Stack

| Layer | Technology |
|-------|------------|
| Frontend | Blazor WebAssembly (standalone) |
| Backend | .NET 10 Minimal API |
| Database | PostgreSQL + Dapper |
| Styling | Tailwind CSS v4 (CLI build) |
| AI | Claude Haiku (Anthropic SDK) — content curation |
| Catalog data | TMDb API |
| Watch providers | JustWatch (via TMDb) |
| Auth | Phone OTP via Twilio |
| Hosting | Azure App Service + Azure PostgreSQL |

## AI Curation

Claude Haiku analyzes the user's watchlist to generate personalized content row strategies. The AI decides *what* rows to show and *how* to title them — TMDb's discover API fills them with actual content from the user's streaming services.

- **Model:** Claude Haiku 4.5 (fast, cost-effective for structured output)
- **Trigger:** Rows regenerate when the watchlist changes (hash-based cache invalidation)
- **Cost tracking:** Per-user token usage logged in `AIUsage` table for pricing analysis
- **Budget:** Configurable monthly cap prevents runaway costs
- **Fallback:** App works fully without AI — TMDb recommendations and trending fill the gap

## Attribution

- Data provided by [TMDb](https://www.themoviedb.org/)
- Watch provider data by [JustWatch](https://www.justwatch.com/)

## License

All rights reserved.
