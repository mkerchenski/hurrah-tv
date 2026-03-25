---
description: Deploy Hurrah.tv to Azure — frontend (Static Web App) and API (App Service)
user-invocable: true
---

# /deploy — Deployment

## Architecture
- **Frontend (WASM):** Azure Static Web Apps — serves static files globally
- **API:** Azure App Service — runs the .NET Minimal API
- **Database:** SQLite (v1) → Azure SQL (production)

## Site Registry

| Component | GitHub Repo | Azure Resource | URL |
|-----------|-------------|----------------|-----|
| Frontend | mkerchenski/hurrah-tv | hurrah-tv-client | https://hurrah.tv |
| API | mkerchenski/hurrah-tv | hurrah-tv-api | https://api.hurrah.tv |

## Commands

```
/deploy status    # Show deployment status for both components
/deploy now       # Trigger deployment (requires user confirmation)
```

## GitHub Actions
- Push to `main` triggers deployment
- Frontend: Build WASM → Deploy to Static Web App
- API: Build → Deploy to App Service staging slot

## Pre-Deploy Checklist
- [ ] All tests pass
- [ ] `dotnet format` clean
- [ ] No secrets in committed files
- [ ] TMDb API key in Azure app settings (not in code)
- [ ] CORS configured for production domain

## Notes
- v1 deployment is manual (not yet automated)
- Production infrastructure will be set up in Phase 5
- SQLite DB will need migration to Azure SQL before production
