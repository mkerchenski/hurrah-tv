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
| Database | Azure Database for PostgreSQL Flexible Server | shared | shared |

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
4. **List active issues** so the user can see what's currently in-flight before swapping. Hurrah.Tv is label-driven (see CLAUDE.md "Issue Tracking"), so use `phase:now` as the In Progress proxy:
   ```bash
   gh issue list --repo mkerchenski/hurrah-tv --label "phase:now" --state open --limit 20
   ```
   Display alongside the health table:
   ```
   In flight (phase:now):
     #66 Queue keyboard support for drag-reorder (a11y)
     #67 Add share button on Details view
   ```
   This is **read-only** — do not mutate any labels from this skill. The user reviews and decides whether the swap is safe.
5. Report status in a table.

**`/deploy now`**
1. Check staging health first. If unhealthy, warn and stop.
2. **Stamp the changelog (mirrors Hurrah's `/version release`).** A prod swap is the moment changes
   become user-visible — and #19 surfaces the changelog to users (the `/changelog` page + the
   new-feature alert banner), so cut a dated release here.
   - Read `CHANGELOG.md`. If the `## [Unreleased]` section has real entries (any `### Category` with
     `- ` items), show the user the draft and confirm.
   - On confirm: replace the `## [Unreleased]` header with `## [<today>]` (use `date +%Y-%m-%d`) and
     insert a fresh empty `## [Unreleased]` above it. Commit to `main` (concise message, e.g.
     `Changelog: cut [Unreleased] → [<today>]`, with the standard `Co-Authored-By` trailer) and push.
   - **Timing matters — the changelog is an embedded resource in the API build** (`/api/changelog`
     reads it from the assembly). The stamp must reach the build *before* it's promoted, so this push
     triggers a fresh staging deploy: **wait for `main_hurrahtv.yml` to finish**
     (`gh run list --workflow main_hurrahtv.yml -R mkerchenski/hurrah-tv --limit 1`, poll to
     completion) so the swapped build carries the dated changelog and the banner fires for users.
   - If `[Unreleased]` is empty (a deploy with no user-visible changes), note that and skip straight to
     the swap — don't stamp an empty release.
3. Use AskUserQuestion to confirm: "This will swap staging to production at hurrah.tv. Proceed?"
4. If confirmed, trigger the swap:
   ```bash
   gh workflow run swap.yml -R mkerchenski/hurrah-tv
   ```
5. Monitor the run:
   ```bash
   gh run list --workflow swap.yml -R mkerchenski/hurrah-tv --limit 1
   ```
6. Report result (including the stamped changelog version, if one was cut).

## Error Handling
- If staging health check fails, show the error and suggest checking the GitHub Actions logs
- If swap fails, the previous production version is still running (swap is atomic)
- To roll back: run `/deploy now` again (staging still has the old production code after a swap)
