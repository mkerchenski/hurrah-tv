# Feedback + Changelog + New-Feature Alerts (#19) — Implementation Plan

> **Status:** Active
> **Tracking issue:** mkerchenski/hurrah-tv#19

## Context

Today we have **zero structured signal from users** — bugs go unreported, feature requests die, and users
get no sense the app is actively improving. #19 adds three connected pieces:
1. **Feedback** — an in-app form (category / message / optional email) stored in the DB, readable in an admin view.
2. **Changelog** — a `/changelog` page rendered from the existing repo-root `CHANGELOG.md` (Keep a Changelog format).
3. **New-feature alert** — a dismissible banner when the latest shipped changelog entry is newer than what the user
   has seen.

Decisions already made: **build feedback in-app** (DB table + admin view, not a GitHub proxy); the changelog is
**parsed into typed entries server-side** (no markdown library — the Keep-a-Changelog structure is regular).

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | yes | `Feedback` table + `UserSettings` column in `DbService.InitializeAsync`; `FeedbackEndpoints`, `ChangelogEndpoints`; admin feedback query |
| HurrahTv.Client | yes | feedback form + admin page + `/changelog` page + alert banner + nav/settings entry points |
| HurrahTv.Shared | yes | `FeedbackSubmission` DTO, `ChangelogEntry` DTO, pure `ChangelogParser`, `LastSeenChangelogVersion` on `UserSettings` |

**DB schema changes:** additive only (expand/contract-safe for the shared-DB slot swap, per
`Learnings/shared-db-slot-swaps-need-backward-compatible-migrations.md`) — a new `Feedback` table and a nullable
`UserSettings.LastSeenChangelogVersion` column. Both via `IF NOT EXISTS` in `InitializeAsync`.
**API contract:** new `/api/feedback`, `/api/admin/feedback`, `/api/changelog`; `UserSettings` DTO gains a field
(ripples to both Api and Client — update the SELECT/INSERT in `DbService` get/save).

---

## Phase 1 — Schema + Shared DTOs (migration-first, independently committable)

- `DbService.InitializeAsync`: add `Feedback` table (`Id` IDENTITY PK, `UserId`, `Category`, `Message`,
  `ContactEmail` NULL, `CreatedAt TIMESTAMPTZ DEFAULT NOW()`) + `IX_Feedback_CreatedAt`; add
  `ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS LastSeenChangelogVersion VARCHAR(50) NULL`
  (mirror the existing `ADD COLUMN IF NOT EXISTS` block ~DbService.cs:185).
- `HurrahTv.Shared/Models/UserSettings.cs`: add `string? LastSeenChangelogVersion`. Update `GetUserSettingsAsync`
  / `SaveUserSettingsAsync` SELECT + upsert to carry it.
- New Shared DTOs: `FeedbackSubmission` (Category, Message, ContactEmail?, plus a honeypot field — see Phase 2),
  `ChangelogEntry` (Version/Date string, `IReadOnlyList<ChangelogSection>` where a section is Category + items).
- **Tests:** Api.Tests — `UserSettings` round-trips `LastSeenChangelogVersion`. **Verify:** `dotnet test`.

## Phase 2 — Feedback API + admin query (independently committable)

- `FeedbackEndpoints.cs` (mirror `EpisodeEndpoints` POST shape): `POST /api/feedback` (`RequireAuthorization`,
  `.RequireRateLimiting(<new per-user policy>)`) → validates, drops silently if the **honeypot** field is non-empty
  (return 200 so bots can't distinguish), else `DbService.SubmitFeedbackAsync`. `GET /api/admin/feedback` on the
  `/api/admin` group (`RequireAuthorization("Admin")`) → list newest-first.
- `Program.cs`: add a `feedback` fixed-window rate-limit policy partitioned per user (mirror
  `TelemetryEndpoints.RateLimitPolicy`, Program.cs:145-157).
- `DbService`: `SubmitFeedbackAsync(userId, submission)` (INSERT) + `GetFeedbackAsync()` (admin SELECT).
- **Tests:** Api.Tests — submit persists; honeypot-filled submission is dropped; rate limit triggers; admin list
  returns; admin endpoint rejects non-admin. **Verify:** `dotnet test`.

## Phase 3 — Feedback form + admin UI (Client; browser-verified)

- Feedback form component (category `<select>`, message textarea, optional email, hidden honeypot input) →
  `ApiClient.SubmitFeedbackAsync` → "thanks, we got it" confirmation. Entry point: "Send Feedback" in
  `Settings.razor` (after the "Tell a friend" section ~line 84) + optionally desktop nav.
- `AdminFeedback.razor` at `/admin/feedback` (`@attribute [Authorize(Policy = "Admin")]`, mirror `AdminUsers.razor`)
  → `ApiClient.GetAdminFeedbackAsync`, list with category/message/time. Add to the admin nav.
- **Tests:** none (Razor/UI). **Verify:** browser — submit feedback, see confirmation, see it in /admin/feedback.

## Phase 4 — Changelog parser + endpoint + page (independently committable)

- `HurrahTv.Shared`: pure `ChangelogParser.Parse(string markdown) → IReadOnlyList<ChangelogEntry>` — splits on
  `## [version/date]` headers, `### Category` subsections, `- ` items. Skip `[Unreleased]` for the "latest shipped"
  concept (it isn't released). Expose a `LatestShippedVersion(entries)` helper.
- `HurrahTv.Api`: embed `CHANGELOG.md` as an `EmbeddedResource` in `HurrahTv.Api.csproj` (robust across deploy — no
  content-root path dependency); `ChangelogEndpoints` `GET /api/changelog` (anonymous) reads the manifest stream,
  parses, returns entries (cache in `IMemoryCache` — it only changes on deploy).
- `HurrahTv.Client`: `/changelog` page renders entries with Razor (typed, no markdown rendering needed);
  `ApiClient.GetChangelogAsync`. Entry point: "What's changed" link.
- **Tests (REQUIRED — pure Shared logic per CLAUDE.md):** `ChangelogParser` — dated release parsed; `[Unreleased]`
  excluded from latest-shipped; multiple categories/items; empty/malformed returns empty; latest-version extraction.
  **Verify:** `dotnet test` + browser.

## Phase 5 — New-feature alert banner (Client; browser-verified)

- `NewFeatureAlertBanner.razor` mounted in `MainLayout.razor` (alongside `<UpdateBanner />`), mirroring
  `UpdateBanner`/`InstallBanner` subscribe/dispose. Shows when `LatestShippedVersion` from `/api/changelog`
  differs from `UserSettings.LastSeenChangelogVersion`. Dismiss → `PUT /api/settings` with
  `LastSeenChangelogVersion = latest`; links to `/changelog`.
- Self-gating per the CLAUDE.md preference (the banner decides its own visibility from settings + changelog, no
  caller flag).
- **Tests:** none (Razor/UI). **Verify:** browser — fresh user sees banner, dismiss persists across reload.

---

## API Considerations
- `/api/feedback` is auth'd + rate-limited + honeypot-guarded; `/api/admin/feedback` uses `RequireAuthorization("Admin")`.
- `/api/changelog` is anonymous (no PII) and memory-cached (content changes only on deploy).

## Blazor WASM Considerations
- Banner subscribes/disposes exactly like `UpdateBanner.razor` (IDisposable; unhook events/timers in `Dispose`).
- Dismissal persists server-side in `UserSettings` (not localStorage) so it follows the user across devices.
- Skeleton/placeholder not needed (banner is additive chrome); avoid CLS by reserving no layout until it decides to show.

## Follow-on
- After landing: append a `CHANGELOG.md` entry for #19 itself (dogfoods the feature) and `/compound` any parser gotchas.
- Out of scope: emailing feedback, GitHub-proxy delivery (decided against), auto-generating changelog from commits.

## Verification (end-to-end)
- `dotnet test HurrahTv.slnx` (Shared parser tests + Api feedback/settings tests).
- `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` before push.
- Browser: submit feedback → admin view; open `/changelog`; new-feature banner shows for a fresh user and its
  dismissal persists across reload.
- PR description: `Closes #19`.
