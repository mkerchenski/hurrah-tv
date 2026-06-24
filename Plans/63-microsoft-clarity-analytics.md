# Microsoft Clarity Product Analytics - Implementation Plan

> **Status:** Complete
> **Tracking issue:** #63

Wire up [Microsoft Clarity](https://clarity.microsoft.com/) for session recordings, heatmaps,
and click/scroll telemetry to inform UX work — free, lightweight, and gated like the rest of our
observability stack (App Insights #206, Sentry #24). Part of mirroring hurrah.dev's standard
analytics stack (Clarity + GA4 + GSC); GA4 + Search Console are tracked on #11.

## Design decisions

- **Production-only gate.** Unlike error monitoring (Sentry runs on staging + prod so we catch
  errors pre-prod), Clarity is gated to **production only** — it mirrors `rum.js`'s prod-only
  stance. Staging sessions are just us testing and would pollute recordings and heatmaps. The
  client script skips `localhost`/`127.0.0.1` and any hostname containing `staging`.
- **Build-time CI substitution (same tier as the Sentry *client* loader key).** WASM `index.html`
  is static and this script runs *pre-boot*, so runtime `appsettings.json` can't reach it (it'd load
  too late, and Client config is downloaded to the browser anyway — equally public). The repo's
  client-config mechanism is therefore build-time substitution: CI replaces `__CLARITY_PROJECT_ID__`
  in `index.html` from the GitHub **variable** `CLARITY_PROJECT_ID`. (Contrast: the *API* Sentry DSN
  correctly lives in Azure app settings — that's server config; this is a public client tag.)
  Project ID: `x90c2vtxpj`.
- **Repo variable, not secret.** The Clarity ID is a *public* tracking ID, so it's a GitHub Actions
  **variable** (`vars.`), not a secret — a secret would be masked to `***` in logs and defeat the
  placeholder-gone guard. An unset variable resolves to empty and the client gate keeps Clarity a
  no-op, so the change is merge-safe before the variable is set.
- **Off the boot critical path (#200).** Loaded async via Clarity's standard tag, behind the host
  gate, so it never blocks the WASM boot on dev/staging.

## Privacy / PII

- Clarity masks **all input-box text in every masking mode** (can't be unmasked) — so the phone
  and OTP `<input>` fields are protected by default.
- Belt-and-suspenders: explicit `data-clarity-mask="true"` on the phone input, the OTP input, and
  the **echoed phone-number span** on the verify step. The span is the load-bearing one — it's
  plain text, so a future switch to Relaxed masking mode would expose it without the attribute.
- Footer disclosure added: "Anonymized session analytics by Microsoft Clarity" linking to the
  Microsoft privacy statement.

## Phase 1 — Client + CI wiring  ✅

- [x] Clarity loader script in `HurrahTv.Client/wwwroot/index.html` (prod-gated, `__CLARITY_PROJECT_ID__` placeholder)
- [x] CI substitution + placeholder-gone guard in `main_hurrahtv.yml` (sourced from `vars.CLARITY_PROJECT_ID`)
- [x] `data-clarity-mask` on phone input, OTP input, and echoed phone span in `Landing.razor`
- [x] Footer session-recording disclosure in `MainLayout.razor`
- Tests: none — all Razor/HTML/CI wiring, no `HurrahTv.Shared` logic. Verify in browser per CLAUDE.md.

## Phase 2 — Provision + verify (operational, kept on #63)  ✅

- [x] Add GitHub Actions repo **variable** `CLARITY_PROJECT_ID` = `x90c2vtxpj`
- [x] Deploy to prod; confirm first session shows up in the Clarity dashboard — verified 2026-06-24
      via Data Export API: 14 sessions / 8 distinct users over 3 days, real paths
      (`/`, `/search`, `/queue`, `/details/*`), prod-host-only.
- [x] Masking confirmed: Clarity masks all input text in every masking mode (unmaskable) +
      explicit `data-clarity-mask` shipped on phone/OTP/echo span.

## Known gap / follow-up candidate → tracked as #218

- The footer is **desktop-only** (`hidden md:block`) — mobile users never see the disclosure.
  Now data-backed: 10 of 14 sessions (3-day window) are mobile/tablet. Spun off to **#218**
  (always-visible disclosure / privacy page) rather than expanding scope here.
