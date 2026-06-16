# Performance Analysis & Observability for Go-Live — Implementation Plan

> **Status:** Draft
> **Phase:** capture-first (v1 Public Launch milestone)
> **Tracking issue:** mkerchenski/hurrah-tv#200
> **Sub-issues:** #201 (client RUM beacon — created via /xplan 2026-06-15)
> **Related:** #24 (App Insights/Sentry), #63 (Clarity), #16 (Azure settings umbrella), #175 (Home load), #3 (bundle)

## Context

The site sporadically takes **10–15 s to load**, other times it's fast (#200). It's intermittent and not reproducible on demand. Investigation this session established:

- **Hosting:** Windows Azure App Service (IIS), one instance serves both the WASM bundle and the API from the same origin; ARR affinity on. **`alwaysOn: true`** — so it is **not** routine idle spin-down.
- **API is fast warm:** `/api/queue` ~35 ms; the dev "1.48 s Home" was mostly the untrimmed-BCL dev server (Release landing boots ~550 ms).
- **SW/cache is already well-designed** (network-first SW keyed to `BuildVersion`, `MapFallbackToFile` set `no-cache` — see `Learnings/service-worker-coexists-with-version-update-flow.md`, `Learnings/mapfallbacktofile-bypasses-static-options.md`). The `dotnet.<hash>.js` 404 seen locally was almost certainly a `dotnet watch` fingerprint race (SW is skipped on localhost), not the prod SW.
- **App Insights is not wired at all** — greenfield.

**Conclusion:** the cheap config lever (Always On) is already pulled, so the cause is intermittent + event-triggered (post-deploy/recycle cold start, dependency cold paths, occasional SW revalidation). We can't guess — we must **capture real slow-load events with a phase breakdown**, then fix the dominant cause. Intended outcome: instrumentation that catches the sporadic loads in the wild, a data-attributed root cause, and a verified fix before go-live.

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | yes | App Insights SDK, Server-Timing middleware, `/api/telemetry` beacon endpoint, `InitializeAsync` cold-start refactor, warm-up |
| HurrahTv.Client | yes | RUM beacon JS in `wwwroot`, Clarity script in `index.html` (prod-gated) |
| HurrahTv.Shared | maybe | a small RUM sample DTO if the beacon posts typed JSON |

**DB schema changes:** none — App Insights is the telemetry sink (no new table). The `/api/telemetry` endpoint forwards to AI, it does not persist to Postgres.
**API contract changes:** new `/api/telemetry` endpoint only (additive); optional Shared DTO.

---

## Phase 1 — App Insights (server) — the linchpin

Greenfield; highest value for the cold-start + dependency hypotheses. This is the Azure-native half of **#24**.

- Add `Microsoft.ApplicationInsights.AspNetCore` to `HurrahTv.Api`; wire in `Program.cs` via connection string in config (**production-only** — don't emit dev/staging noise, or use a separate AI resource for staging). Connection string is an App secret, not committed.
- Enable request + dependency auto-collection (Npgsql/`HttpClient` to TMDb are auto-tracked) so **DB and TMDb latency** and **app-start/cold-start** are visible.
- Stamp `BuildVersion` (already in `appsettings.json`) as the AI cloud role / version tag so telemetry is per-deploy attributable.
- **Verify:** drive a few requests against staging; confirm requests, dependencies (Postgres + TMDb), and a cold-start (restart slot → first request) appear in the AI resource.
- **Tests:** none (infra wiring); gate is "telemetry visible in AI."

## Phase 2 — Server-Timing header + `/api/telemetry` beacon + client RUM

Capture the **client-side** phase breakdown for slow loads and tie it to server cost.

- **API:** middleware that emits a `Server-Timing` response header attributing server-side cost (app warm/cold, DB, TMDb) so the client beacon can separate "server was slow" from "bundle/boot was slow". New `/api/telemetry` minimal endpoint (anonymous, **rate-limited + payload-capped**, honeypot-free since it's machine-posted) that forwards a RUM sample to App Insights as a custom event.
- **Client:** ~30-line `wwwroot/js/rum.js` that, after load, reads Navigation Timing + Resource Timing (DNS/TLS/TTFB/bundle-download/boot/first-render) and **POSTs only when total load exceeds a threshold (e.g. > 3 s)** or on a small sample rate — so we capture the sporadic 10–15 s events without flooding. Registered in `index.html`, **prod-host-gated** (skip localhost/staging or tag env).
- **Verify:** throttle the network in Chrome DevTools to force a slow load; confirm a sample lands in AI with a phase breakdown; confirm normal-fast loads are not beaconed.
- **Tests:** if the threshold/sampling decision is pure logic, extract to `HurrahTv.Shared` and unit-test it; the JS/wiring is browser-verified.

## Phase 3 — Microsoft Clarity (#63)

Qualitative: literally watch a slow load happen.

- Add the Clarity tag to `index.html`, **prod-host-gated**; mask phone/OTP inputs (`data-clarity-mask`); update footer/privacy text. Per #63's acceptance criteria.
- **Verify:** a session shows up in the Clarity dashboard within an hour; OTP/phone masked.
- **Tests:** none.

## Phase 4 — Fix the dominant cause (data-driven)

Only after Phases 1–3 produce attributed samples. Candidates, ranked by current suspicion:

- **Cold start:** move `DbService.InitializeAsync` DDL off the request hot path — run-once guard / `IHostedService` startup task so the first user request doesn't wait on `CREATE TABLE IF NOT EXISTS` (mind `Learnings/shared-db-slot-swaps-need-backward-compatible-migrations.md` — keep migrations expand/contract for slot swaps).
- **Post-deploy warm-up:** CI hits `/health` (or a warm-up path) after deploy/swap so the first real user isn't cold. Ties into #16.
- **Cache headers:** audit `_framework/*` server cache headers (fingerprinted assets → `immutable`) so first-load + SW population is optimal; confirm the SW `no-cache`-before-`?v=`-immutable ordering still holds.
- **Re-verify** each fix against AI/RUM over a monitoring window; #200's acceptance criteria is "sporadic slow loads no longer reproduce."

---

## API Considerations
- `/api/telemetry` is anonymous + rate-limited + size-capped (abuse surface); forwards to AI, never writes Postgres.
- Background/startup work uses `IServiceScopeFactory` / `IHostedService`, not request-thread init.
- App Insights connection string is an Azure App secret (prod slot), mirroring how TMDb/JWT secrets are handled — never committed.

## Blazor WASM Considerations
- RUM beacon is plain JS in `wwwroot` (not a Blazor component) — runs regardless of WASM boot success, so it still captures loads where boot itself was slow. Load it early in `index.html` like `js/version.js`.
- Keep it tiny — bundle weight still matters for first paint even if download is SW-cached after.

## External Integrations
- App Insights: prod-only (or separate staging resource) to avoid noise/cost.
- Clarity: free; prod-host-gated; mask sensitive fields.
- No TMDb/Anthropic/Twilio changes.

## Follow-on
- Open a **new tracking issue for the client RUM beacon** (Phase 2) — none exists yet; link to #200.
- After landing: `/compound` any non-obvious cold-start / AI-wiring learnings.
- This plan is separate from the `#3`/`#175` bundle branch (don't mix).
