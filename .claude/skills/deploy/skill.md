---
description: Deploy Hurrah.tv — swap staging to production or check status
argument-hint: <status|now>
user-invocable: true
---

# /deploy — Hurrah.tv Deployment

**Operation:** $ARGUMENTS

## Architecture

Single App Service serves both the Blazor WASM client (static files) and the .NET Minimal API.

| Component | Azure Resource | Staging URL | Production URL |
|-----------|---------------|-------------|---------------|
| App (API + WASM) | HurrahTv-Api | hurrahtv-api-staging.azurewebsites.net | hurrah.tv |
| Database | hurrahtv (Azure SQL) | shared | shared |

**Deploy flow:**
1. Push to `main` → GitHub Actions builds both projects and deploys to the **staging slot**
2. `/deploy now` → swaps staging slot to production (both client + API promote together)

## Commands

**`/deploy status`**
1. Check the latest GitHub Actions run:
   ```bash
   gh run list --workflow main_hurrahtv.yml -R mkerchenski/hurrah-tv --limit 3
   ```
2. Check staging health:
   ```bash
   curl -sk https://hurrahtv-api-staging.azurewebsites.net/api/health
   ```
3. Check production health:
   ```bash
   curl -sk https://hurrahtv-api.azurewebsites.net/api/health
   ```
4. Report status in a table.

**`/deploy now`**
1. Check staging health first. If unhealthy, warn and stop.
2. Use AskUserQuestion to confirm: "This will swap staging to production at hurrah.tv. Proceed?"
3. If confirmed, trigger the swap:
   ```bash
   gh workflow run swap.yml -R mkerchenski/hurrah-tv
   ```
4. Monitor the run:
   ```bash
   gh run list --workflow swap.yml -R mkerchenski/hurrah-tv --limit 1
   ```
5. Report result.

## Error Handling
- If staging health check fails, show the error and suggest checking the GitHub Actions logs
- If swap fails, the previous production version is still running (swap is atomic)
- To roll back: run `/deploy now` again (staging still has the old production code after a swap)
