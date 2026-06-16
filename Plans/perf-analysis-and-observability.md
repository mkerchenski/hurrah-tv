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

- ✅ `ResponseTimingMiddleware` (`HurrahTv.Api/Middleware/`) — `Stopwatch.GetTimestamp()` at entry, writes `Server-Timing: app;dur=<ms>` via `Response.OnStarting` (captures time-to-first-byte, the slice the beacon subtracts from TTFB). Registered as the **outermost** middleware (right after the dev exception page) so it brackets the whole request.
- ✅ **CORS exposure** — added `.WithExposedHeaders("Server-Timing")` to the default policy so `rum.js` can read it cross-origin in dev (`:7267`→`:7201`); same-origin in prod exposes it anyway.
- ✅ **Verified locally** (`curl -ksi`): `/api/health` → `Server-Timing: app;dur=39.9` (cold first request), `/api/version` → `app;dur=2.7` (warm) — the cold/warm gap is exactly the signal the beacon will attribute.
- No unit test: middleware is browser/integration-verified per CLAUDE.md; verified by curl above.

## Phase 3 — Client RUM beacon (#201, client half)

- ✅ `HurrahTv.Client/wwwroot/js/rum.js` — on `load`, reads Navigation Timing + the slowest `_framework/*` resource (bundle proxy) + the `app` entry from `nav.serverTiming`. Beacons via `navigator.sendBeacon` only when total > **3 s** OR a **1%** random sample; normal-fast loads send nothing. **Prod-host-gated** (skip localhost/staging), wrapped so it can never throw into the page.
- ✅ Loaded as a **classic `<script defer src="js/rum.js">`** in `index.html` `<head>` (not a Blazor ES module) so it fires even when a slow WASM boot is the problem.
- ✅ `POST /api/telemetry` (`HurrahTv.Api/Endpoints/TelemetryEndpoints.cs`) — anonymous, binds the body manually off `HttpContext` so the **4 KB size cap** runs before the read (413), garbage JSON → 400, **per-IP fixed-window rate limit** (10/min, partitioned on `X-Forwarded-For`→`RemoteIpAddress`) via `AddRateLimiter`/`UseRateLimiter`/`RequireRateLimiting` scoped to this endpoint. Forwards to App Insights via `TelemetryClient.TrackEvent` **only when configured** (accept-and-drop otherwise). Page URL re-scrubbed through `PiiRedactionProcessor.Redact`.
  - **3.x note:** `EventTelemetry` has no `Metrics` dict and `TrackEvent` lost its 3-arg overload — timing phases go in as string **custom dimensions** (`customDimensions.totalMs` etc.), which is what per-load diagnosis queries anyway.
- SW interplay safe as predicted: `/api/*` network-only so the POST isn't cached; `js/rum.js` served `must-revalidate`; no integrity attribute added.
- ✅ **Tests** (`HurrahTv.Api.Tests/TelemetryEndpointTests.cs`): valid → 202, oversized → 413, garbage → 400, 11th/min → 429 (each test on a distinct `X-Forwarded-For` partition). 4 green; full suite 128+46+39 green.
- **Verify** (pending staging): Chrome DevTools throttled reload on the deployed host → a `RumLoad` event with the phase breakdown (incl. `serverMs` from `Server-Timing`) in App Insights; a fast load sends nothing.

## Phase 4 — Capture window + diagnosis (#200 AC#2)

No code. Let instrumentation run; watch App Insights for a real 10–15s load and attribute it to a phase (cold-start vs bundle re-download vs boot) using the `Server-Timing` split + Resource Timing. Record the finding on #200. **Gate Phase 5 on this data** — don't pre-commit a fix.

- [ ] **Validate the PII redaction against real telemetry** (folded from /xsimplify): the scrubber's 10-digit threshold and hardcoded `url.*`/`http.*` tag list are heuristics. Spot-check live `customDimensions.url` values for (a) leaked phone numbers/tokens the rules missed, and (b) legitimate long numeric IDs wrongly redacted. If wrong, tune the threshold / tag list — consider making them config-driven only if the data shows it's needed.

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
