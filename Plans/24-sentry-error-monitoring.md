# Sentry Error Monitoring - Implementation Plan

> **Status:** Active
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

- [ ] `Sentry.AspNetCore` package (6.6.0).
- [ ] `builder.WebHost.UseSentry(...)` gated on `SENTRY_DSN` / `Sentry:Dsn` (non-placeholder).
- [ ] `Release = buildVersion`, `SendDefaultPii = false`, `BeforeSend` scrubs `Request.Url` +
      `QueryString` via `PiiRedactionProcessor.Redact`.
- [ ] `Sentry` placeholder section in `appsettings.json` (`YOUR_SENTRY_DSN`).
- **Tests:** none new — `BeforeSend` reuses `PiiRedactionProcessor.Redact`, already covered by
  `PiiRedactionProcessorTests`. Wiring is DI scaffolding (CLAUDE.md: verify in browser, no test).

## Phase 2 — Client (Sentry browser SDK) ✅ code

- [ ] Self-gating loader in `index.html`: prod host + real `__SENTRY_DSN__` only (placeholder /
      dev / staging → no-op). Pinned CDN bundle `browser.sentry-cdn.com/10.58.0/bundle.min.js`.
- [ ] `release` = the stamped `__BUILD_VERSION__`, `sendDefaultPii: false`, `tracesSampleRate: 0`
      (errors only — App Insights owns performance/RUM).

## Phase 3 — CI

- [ ] In the "Cache-bust" step, `sed` `__SENTRY_DSN__` → `${{ secrets.SENTRY_DSN_CLIENT }}` and
      `__BUILD_VERSION__` → `$SHORT_SHA` in `wwwroot/index.html`. Unset secret → empty → no-op.

## Out of scope / owner = you (account + infra steps)

- Create the Sentry project under the `hurrah-web` org; get the API DSN + the client DSN.
- Set `SENTRY_DSN` app-setting on the App Service (staging + prod slots); set the
  `SENTRY_DSN_CLIENT` GitHub secret.
- Alert rules (Slack/email on unhandled prod exceptions) — Sentry UI.
- Source maps: **N/A for our code** — we don't bundle/minify our own JS (`rum.js` etc. ship
  as-is, readable), and the `_framework` JS is Microsoft's. Pin the CDN bundle version when the
  DSN lands and confirm it resolves.
- Verify: trigger a test exception in staging once the DSN is set; confirm it lands in Sentry.
