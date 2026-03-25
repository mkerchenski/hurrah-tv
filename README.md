# Hurrah.tv

One queue for everything you watch. Search what's available across your streaming services and manage a single unified watchlist.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![Blazor WASM](https://img.shields.io/badge/Blazor-WebAssembly-512BD4) ![SQLite](https://img.shields.io/badge/SQLite-003B57)

## What it does

Pick your streaming services (Netflix, Hulu, Disney+, etc.), then browse and search content across all of them in one place. Add shows and movies to a single queue instead of jumping between apps.

**Current status:** Early prototype (Phase 1 complete — search, browse, queue)

## Architecture

Three-project solution: Blazor WebAssembly frontend + .NET Minimal API backend + shared DTOs.

```
HurrahTv.Api/        .NET Minimal API — TMDb proxy, SQLite database
HurrahTv.Client/     Blazor WASM — runs entirely in the browser
HurrahTv.Shared/     DTOs shared between API and Client
```

### API Endpoints

| Endpoint group | Purpose |
|----------------|---------|
| `/api/search`  | TMDb search proxy, trending, discover by provider |
| `/api/details` | Show/movie details with watch provider availability |
| `/api/queue`   | CRUD for the user's watchlist |
| `/api/services`| Manage which streaming services the user subscribes to |

### Client Pages

| Page | Description |
|------|-------------|
| Home | Trending content + popular titles per streaming service |
| Search | Debounced search with All/TV/Movies filters |
| Details | Backdrop, poster, metadata, seasons, available services |
| Queue | Watchlist with Queued/Watching/Watched statuses |
| Settings | Streaming service selection |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A [TMDb API key](https://www.themoviedb.org/settings/api) (free)

### Setup

1. Clone the repo:
   ```bash
   git clone https://github.com/mkerchenski/hurrah-tv.git
   cd hurrah-tv
   ```

2. Add your TMDb API key. Create `HurrahTv.Api/appsettings.Development.json`:
   ```json
   {
     "Tmdb": {
       "ApiKey": "your-tmdb-api-key-here"
     }
   }
   ```

3. Run both projects simultaneously (each in its own terminal):
   ```bash
   # Terminal 1 — API
   cd HurrahTv.Api
   dotnet watch --launch-profile https

   # Terminal 2 — Client
   cd HurrahTv.Client
   dotnet watch --launch-profile https
   ```

4. Open https://localhost:7267 in your browser.

## Tech Stack

| Layer | Technology |
|-------|------------|
| Frontend | Blazor WebAssembly (standalone) |
| Backend | .NET 10 Minimal API |
| Database | SQLite + Dapper |
| Styling | Tailwind CSS (CDN) |
| Catalog data | TMDb API |
| Watch providers | JustWatch (via TMDb) |

## Attribution

- Data provided by [TMDb](https://www.themoviedb.org/)
- Watch provider data by [JustWatch](https://www.justwatch.com/)

## License

All rights reserved.
