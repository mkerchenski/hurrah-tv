# Perf Analysis & Observability — Implementation Plan

> **Status:** Active
> **Tracking issue:** mkerchenski/hurrah-tv#200
> **Related issues:** #24 (App Insights server), #201 (RUM beacon)

## Context

The site sometimes takes 10–15s to load, intermittently and not reliably reproducible (#200). The cheap config lever (App Service **Always On**) is already pulled, so the cause must be **captured in the wild** before it can be fixed. Suspected phases: server cold-start (the `await db.InitializeAsync()` DDL blocks startup at `Program.cs:120`), service-worker / bundle re-download after deploy, or cold dependency paths (Postgres/TMDb).

This plan is the **instrument-first** track: stand up the observability so a real slow load gets attributed to a *specific phase*, then fix the dominant cause with proof. Decision already taken: **App Insights, not Sentry** — #201 is written against App Insights as its sink, it's already provisioned in Azure, and the #200 goal is phase-attribution (custom metrics/timing), which is App Insights' wheelhouse. Sentry's exception-DX edge is a broader want that should not block #200; defer it with a comment on #24.

**No DB schema changes** — telemetry goes to App Insights, not Postgres.

## Affected Projects

| Project          | Touched | Notes                                                                                  |
|------------------|---------|----------------------------------------------------------------------------------------|
| HurrahTv.Api     | yes     | App Insights DI + PII scrubber, `Server-Timing` middleware, `POST /api/telemetry`, `appsettings.json` placeholder, `.csproj` package |
| HurrahTv.Client  | yes     | `wwwroot/js/rum.js`, one early `<script>` in `index.html`                              |
| HurrahTv.Shared  | no      | Telemetry payload is a JS→Api contract deserialized into an Api-local record; no ApiClient/Blazor involvement, so no Shared DTO ripple |

---

## Phase 1 — Server-side App Insights (#24, scoped to #200)

Foundation: the sink #201 reports into.

> **⚠ SDK 3.x discovery (during implementation):** `Microsoft.ApplicationInsights.AspNetCore` resolved to **3.1.2**, a post-cutoff major that is now **OpenTelemetry-based**. The classic pipeline — `ITelemetryInitializer`, `ITelemetryProcessor`, `TelemetryConfiguration` — is **removed**. Customization is now a custom OpenTelemetry `BaseProcessor<Activity>` registered via `ConfigureOpenTelemetryTracerProvider`. `AddApplicationInsightsTelemetry()` and `TelemetryClient.TrackEvent` (Phase 3) still exist. Bullets below reflect the 3.x reality. (Migration guidance: github.com/microsoft/ApplicationInsights-dotnet/blob/main/MigrationGuidance.md)

- ✅ Added `Microsoft.ApplicationInsights.AspNetCore` 3.1.2 to `HurrahTv.Api.csproj` (pulls OpenTelemetry transitively).
- ✅ Register `builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = …)` near the other service registrations, **enabled only when a real connection string is present** (presence + non-`YOUR_` placeholder check), read the env-branch way, NOT `?? fallback` (`Learnings/aspnet-config-get-null-coalesce-trap.md`). Azure App Service supplies `APPLICATIONINSIGHTS_CONNECTION_STRING`; committed `appsettings.json` carries a `"YOUR_APPINSIGHTS_CONNECTION_STRING"` placeholder under a new top-level `ApplicationInsights` key. Dev/local sends nothing.
- ✅ **PII scrubbing** — `PiiRedactionProcessor : BaseProcessor<Activity>` (`HurrahTv.Api/Telemetry/`) redacts the `url.query`/`url.path`/`url.full`/`http.url`/`http.target` span tags (the only PII vector: OTel records no bodies/headers). Redacts sensitive query-param values + collapses 10+ digit runs (phone-length; spares shorter TMDb ids). Registered via `ConfigureOpenTelemetryTracerProvider`.
- ✅ **Release tagging** — `ConfigureResource(r => r.AddService("HurrahTv.Api", serviceVersion: buildVersion))` sets `service.version` to the CI build SHA (replaces `Context.Component.Version`).
- ✅ **Tests** (`HurrahTv.Api.Tests/PiiRedactionProcessorTests.cs`): 6 tests driving the processor through a real `Activity` — phone/token redacted, TMDb id preserved, non-URL tags untouched. All green.
- **Verify** (pending deploy): on staging, trigger a trace, confirm it appears in App Insights with the build SHA and no PII.

## Phase 2 — `Server-Timing` response header (#201, server half)

The linchpin for splitting server-cost from client-cost.

- Add a small `ResponseTimingMiddleware` mirroring `OgPreviewMiddleware` (`HurrahTv.Api/Middleware/`, registered at `Program.cs:89`): `Stopwatch` around `await next(ctx)`, write `Server-Timing: app;dur=<ms>` via `ctx.Response.OnStarting(...)` (headers must be set before the body flushes). Register early in the pipeline so it brackets the most work.
- **CORS exposure**: `Server-Timing` is same-origin in prod (one instance serves client + API) so it's exposed automatically — but in dev the client (`:7267`) hits the API (`:7201`) cross-origin, so add `Server-Timing` to `Access-Control-Expose-Headers` (extend the CORS policy at `Program.cs:47–50`). Without this, `rum.js` reads `undefined` locally and looks broken when it isn't.
- **Verify**: `curl -I` staging — confirm `Server-Timing` present on a navigation and on `/api/*`.

## Phase 3 — Client RUM beacon (#201, client half)

- `HurrahTv.Client/wwwroot/js/rum.js`: on `load`/`pagehide`, read Navigation Timing + Resource Timing (DNS/TLS/TTFB/bundle-download/boot/first-render), fold in `Server-Timing` from the navigation `PerformanceResourceTiming.serverTiming` entry. Beacon via `navigator.sendBeacon` **only when total load exceeds a threshold (~3s) or on a small sample rate** — normal-fast loads are never sent. **Host-gate to prod** (skip `localhost`/`staging`, or tag by env), mirroring the SW registration guard at `index.html:70`.
- **Load as a classic early `<script>` in `index.html`** (not an ES module imported via Blazor interop like `version.js`) — a slow *boot* is one of the suspects, so the beacon must run even when Blazor doesn't finish booting. Navigation Timing is available post-`load` regardless.
- `POST /api/telemetry`: anonymous (`MapGroup(...).AllowAnonymous()` per `AuthEndpoints.cs:10`), typed request record, forwards the sample to App Insights as a custom event (`TelemetryClient.TrackEvent`). **Payload-capped** (reject oversized bodies — defaults are 30 MB; cap to a few KB) and **rate-limited** (greenfield — add `AddRateLimiter` + a per-IP fixed-window policy, `RequireRateLimiting` on this endpoint only). Env-tag the event so localhost/staging is filterable.
- SW interplay is already safe: `/api/*` is network-only (`service-worker.js:48`) so the POST is never cached; `js/rum.js` is cache-first/fingerprinted so it can't go stale (`Learnings/service-worker-coexists-with-version-update-flow.md`). Do **not** add a subresource-integrity attribute to the `rum.js` tag (`Learnings/static-asset-integrity-blocks-hot-edited-js.md`).
- **Tests** (`HurrahTv.Api.Tests`): the `/api/telemetry` endpoint — oversized payload → rejected, valid payload → 200 + forwarded, rate-limit trips after N. (Threshold/sampling logic is plain client JS, verified in-browser, not in a C# test project.)
- **Verify**: Chrome DevTools throttled-network reload → confirm a sample with a full phase breakdown (incl. the server slice from `Server-Timing`) lands in App Insights; a fast load sends nothing.

## Phase 4 — Capture window + diagnosis (#200 AC#2)

No code. Let instrumentation run; watch App Insights for a real 10–15s load and attribute it to a phase (cold-start vs bundle re-download vs boot) using the `Server-Timing` split + Resource Timing. Record the finding on #200. **Gate Phase 5 on this data** — don't pre-commit a fix.

## Phase 5 — Fix the dominant cause (#200 AC#3/#4)

Candidate fixes, picked by Phase 4 data (do not build blind):
- Move `InitializeAsync` DDL off the cold-start hot path (lazy/background; it blocks at `Program.cs:120`).
- Audit `_framework` cache headers + SW update strategy (the observed `dotnet.<hash>.js` 404→retry); note PR #202 already shrank the bundle 2.9→2.46 MB.
- Post-deploy warm-up ping to the existing `/api/health` (`Program.cs:136`) from the `/deploy` skill / `swap.yml`, so the first real user doesn't eat cold-start.

**Verify (AC#4)**: sporadic slow loads no longer reproduce over a monitoring window in App Insights.

---

## Hurrah.tv Considerations

- **API**: mutating endpoints aren't involved; `/api/telemetry` is fire-and-forget from the client's view. Background forwarding (if any) uses `IServiceScopeFactory.CreateAsyncScope()` for fresh transients — but `TrackEvent` is cheap/sync, so likely inline.
- **Config trap**: enable App Insights via an env-branch / presence check, never `?? fallback` (`Learnings/aspnet-config-get-null-coalesce-trap.md`).
- **Cache pipeline**: `MapFallbackToFile` has its own `StaticFileOptions` separate from `UseStaticFiles` (`Learnings/mapfallbacktofile-bypasses-static-options.md`) — relevant if Phase 5 touches `_framework`/index.html headers.
- **Formatter gate before push**: `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx`.

## Follow-on actions

- Drop a comment on #24 recording "App Insights chosen for #200; Sentry deferred."
- After landing instrumentation, invoke `/compound` to capture any non-obvious App Insights / `Server-Timing` / Blazor-boot-timing learnings.
- Phases 1–3 are each independently committable and leave the system working.
