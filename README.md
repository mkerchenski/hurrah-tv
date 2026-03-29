# Hurrah.tv

AI-curated streaming across all your services. One smart watchlist that learns what you love and surfaces what to watch next.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![Blazor WASM](https://img.shields.io/badge/Blazor-WebAssembly-512BD4) ![SQL Server](https://img.shields.io/badge/SQL_Server-CC2927) ![Claude AI](https://img.shields.io/badge/Claude_AI-Anthropic-D4A574)

## What it does

Hurrah.tv is an opinionated streaming platform that curates content across Netflix, Hulu, Disney+, Prime Video, Max, Peacock, Paramount+, and Apple TV+. It learns from your watch history and preferences to build a personalized home feed — like opening one app instead of eight.

- **AI-powered curation** — Claude analyzes your taste to generate themed content rows ("High-Stakes Hospital Nights", "Slow-Burn Thrillers That Hook Fast")
- **Smart watchlists** — Track what you're watching, what you want to watch, and what you've loved. Every interaction feeds better recommendations.
- **New episode alerts** — Shows in your list with new or upcoming episodes are flagged automatically
- **"I've Seen This"** — One-click signal building while browsing. Mark shows you've already watched to improve recommendations without managing a list.
- **Balanced discovery** — Content is interleaved across your services so no single provider dominates your feed

**Live at:** [hurrah.tv](https://hurrah.tv)

## Architecture

Three-project solution: Blazor WebAssembly frontend + .NET Minimal API backend + shared DTOs.

```
HurrahTv.Api/        .NET Minimal API — TMDb proxy, Claude AI curation, SQL Server
HurrahTv.Client/     Blazor WASM — runs entirely in the browser
HurrahTv.Shared/     DTOs shared between API and Client
```

### Home Page Feed

The home page renders Netflix-style horizontal content rows, each powered by a different data source:

| Row type | Source | Example |
|----------|--------|---------|
| New Episodes | User's watchlist + TMDb air dates | Shows with episodes in the last 7 days |
| Continue Watching | User's "Watching" list | Items sorted by recent activity |
| AI-Curated | Claude AI + TMDb discover | "High-Stakes Hospital Nights" |
| New This Season | TMDb discover (date-filtered) | Recently aired TV across your services |
| Trending | TMDb popularity + recency boost | Popular shows, newer ones first |
| Because You Liked X | TMDb recommendations API | Similar shows to your favorites |

### API Endpoints

| Endpoint group | Purpose |
|----------------|---------|
| `/api/search` | TMDb search proxy, trending, discover, recommendations |
| `/api/details` | Show/movie details with watch providers and episode info |
| `/api/queue` | Watchlist CRUD — statuses, ratings, progress, "seen it" |
| `/api/curation` | AI-curated row generation, usage tracking |
| `/api/services` | Manage subscribed streaming services |
| `/api/auth` | Phone OTP login (SMS-based, no passwords) |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (for Tailwind CSS build)
- SQL Server (LocalDB or full instance)
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
     "ConnectionStrings": { "Default": "Server=YOUR_SERVER;Database=HurrahTv;Trusted_Connection=True;TrustServerCertificate=True;" },
     "Tmdb": { "ApiKey": "your-tmdb-api-key" },
     "Jwt": { "Key": "your-base64-jwt-key" },
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
| Database | SQL Server + Dapper |
| Styling | Tailwind CSS v4 (CLI build) |
| AI | Claude Haiku (Anthropic SDK) — content curation |
| Catalog data | TMDb API |
| Watch providers | JustWatch (via TMDb) |
| Auth | Phone OTP via Twilio |
| Hosting | Azure App Service + Azure SQL |

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
