# Sentry Error Monitoring - Implementation Plan

> **Status:** Complete
> **Tracking issue:** #24

Add Sentry exception monitoring alongside the App Insights telemetry already shipped in
#206. Launch decision (v1, on #24): run **both** stacks — App Insights for phase-attribution
RUM (#200/#201), Sentry for exception DX + alerting. This plan covers the Sentry half.

## Design principles (mirror what #206 established)

- **DSN-gated, no-op until provisioned.** Same shape as App Insights' connection-string gate
  (`Program.cs`): read the DSN from config, and if it's missing or the committed `YOUR_…`
  placeholder, register nothing. Safe to merge before the Sentry project exists.
- **Reuse the PII redactor.** Sentry's server-side `BeforeSend` runs the request URL through
  the already-unit-tested `PiiRedactionProcessor.Redact` rather than duplicating the phone/OTP/
  token regexes. One canonical scrub rule for both sinks.
- **Client = classic JS SDK, not the .NET WASM SDK.** Same rationale as `rum.js`: a failed WASM
  boot is a real failure mode, and a .NET-side handler is dead if the runtime never starts. A
  browser script loads independently of Blazor and can report "the app never booted."
- **Release = the CI short SHA**, identical to App Insights `service.version` and `BuildVersion`.

## Phase 1 — API (Sentry.AspNetCore) ✅ code

- [x] `Sentry.AspNetCore` package (6.6.0).
- [x] `builder.WebHost.UseSentry(...)` gated on `SENTRY_DSN` / `Sentry:Dsn` (non-placeholder).
- [x] `Release = buildVersion`, `SendDefaultPii = false`, `BeforeSend` scrubs `Request.Url` +
      `QueryString` via `PiiRedactionProcessor.Redact`.
- [x] `Sentry` placeholder section in `appsettings.json` (`YOUR_SENTRY_DSN`).
- **Tests:** none new — `BeforeSend` reuses `PiiRedactionProcessor.Redact`, already covered by
  `PiiRedactionProcessorTests`. Wiring is DI scaffolding (CLAUDE.md: verify in browser, no test).

## Phase 2 — Client (Sentry browser SDK) ✅ code

- [x] Self-gating loader in `index.html`: prod host + real `__SENTRY_LOADER_KEY__` only
      (placeholder / dev → no-op). Lazy **Loader Script** `https://js.sentry-cdn.com/<key>.min.js`
      — a ~1.5 KB async stub that installs error handlers immediately (catches failed-boot
      errors) and fetches the full SDK only when an error fires. No version pin (Sentry serves
      the SDK behind the key). Inserted dynamically so it never blocks the WASM boot path (#200).
- [x] `release` = the stamped `__BUILD_VERSION__`, `sendDefaultPii: false`, `tracesSampleRate: 0`
      (errors only — App Insights owns performance/RUM).
- [x] Client PII scrub: `beforeSend`/`beforeBreadcrumb` strip query strings + fragments off
      `event.request.url` and breadcrumb URLs (wholesale, mirroring the server redactor's intent
      without porting its regex). Note: staging reports too (env-tagged), not just prod.

## Phase 3 — CI

- [x] In the "Cache-bust" step, `sed` `__SENTRY_LOADER_KEY__` → `${{ secrets.SENTRY_LOADER_KEY }}`
      and `__BUILD_VERSION__` → `$SHORT_SHA` in `wwwroot/index.html`. Unset secret → empty → the
      client gate keeps Sentry a no-op.

## Out of scope / owner = you (account + infra steps)

- Create the Sentry project under the `hurrah-web` org; get the API **DSN** and the client
  **Loader Script key** (the public-key fragment of the DSN — Sentry vends it as a loader, not a
  separate client DSN).
- Set `SENTRY_DSN` app-setting on the App Service (staging + prod slots); set the
  `SENTRY_LOADER_KEY` GitHub secret. **(Done — both provisioned during wiring.)**
- Alert rules (Slack/email on unhandled prod exceptions) — Sentry UI. **(Done — confirmed 2026-06-24.)**
- Source maps: **N/A for our code** — we don't bundle/minify our own JS (`rum.js` etc. ship
  as-is, readable), and the `_framework` JS is Microsoft's.
- Verify: **✅ confirmed 2026-06-24** — 6 real production issues captured across both sinks
  (API: `Npgsql`/`Twilio`; Client: `TypeError`/WASM-boot). PII masking verified live
  (phone rendered `+628519411XXXX` in HURRAH-TV-1). Better signal than a synthetic test.
  Findings triaged: DB timeouts → evidence added to #200; Twilio region → #219.
