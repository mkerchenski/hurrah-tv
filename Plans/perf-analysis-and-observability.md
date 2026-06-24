# Performance Analysis & Observability for Go-Live — Implementation Plan

> **Status:** Active — capture phases (1–3) shipped; **Phase 4 (DB connect-timeout fix) ready** (data-attributed 2026-06-24)
> **Phase:** fix-the-dominant-cause (v1 Public Launch milestone)
> **Tracking issue:** mkerchenski/hurrah-tv#200
> **Sub-issues:** #201 (client RUM beacon — created via /xplan 2026-06-15)
> **Related:** #24 (Sentry — shipped), #206 (App Insights — shipped), #63 (Clarity — shipped), #16 (Azure settings umbrella), #175 (Home load), #3 (bundle)
> **Capture-phase outcome:** Sentry caught the first attributed cause — `Npgsql` connection-acquisition timeouts on `/api/queue` (HURRAH-TV-3). See Phase 4.

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

## Phase 1 — App Insights (server) — the linchpin  ✅ SHIPPED (#206)

Greenfield; highest value for the cold-start + dependency hypotheses. This is the Azure-native half of **#24**.

- Add `Microsoft.ApplicationInsights.AspNetCore` to `HurrahTv.Api`; wire in `Program.cs` via connection string in config (**production-only** — don't emit dev/staging noise, or use a separate AI resource for staging). Connection string is an App secret, not committed.
- Enable request + dependency auto-collection (Npgsql/`HttpClient` to TMDb are auto-tracked) so **DB and TMDb latency** and **app-start/cold-start** are visible.
- Stamp `BuildVersion` (already in `appsettings.json`) as the AI cloud role / version tag so telemetry is per-deploy attributable.
- **Verify:** drive a few requests against staging; confirm requests, dependencies (Postgres + TMDb), and a cold-start (restart slot → first request) appear in the AI resource.
- **Tests:** none (infra wiring); gate is "telemetry visible in AI."

## Phase 2 — Server-Timing header + `/api/telemetry` beacon + client RUM  ✅ SHIPPED (#201)

Capture the **client-side** phase breakdown for slow loads and tie it to server cost.

- **API:** middleware that emits a `Server-Timing` response header attributing server-side cost (app warm/cold, DB, TMDb) so the client beacon can separate "server was slow" from "bundle/boot was slow". New `/api/telemetry` minimal endpoint (anonymous, **rate-limited + payload-capped**, honeypot-free since it's machine-posted) that forwards a RUM sample to App Insights as a custom event.
- **Client:** ~30-line `wwwroot/js/rum.js` that, after load, reads Navigation Timing + Resource Timing (DNS/TLS/TTFB/bundle-download/boot/first-render) and **POSTs only when total load exceeds a threshold (e.g. > 3 s)** or on a small sample rate — so we capture the sporadic 10–15 s events without flooding. Registered in `index.html`, **prod-host-gated** (skip localhost/staging or tag env).
- **Verify:** throttle the network in Chrome DevTools to force a slow load; confirm a sample lands in AI with a phase breakdown; confirm normal-fast loads are not beaconed.
- **Tests:** if the threshold/sampling decision is pure logic, extract to `HurrahTv.Shared` and unit-test it; the JS/wiring is browser-verified.

## Phase 3 — Microsoft Clarity (#63)  ✅ SHIPPED (#63, verified live 2026-06-24)

Qualitative: literally watch a slow load happen.

- Add the Clarity tag to `index.html`, **prod-host-gated**; mask phone/OTP inputs (`data-clarity-mask`); update footer/privacy text. Per #63's acceptance criteria.
- **Verify:** a session shows up in the Clarity dashboard within an hour; OTP/phone masked.
- **Tests:** none.

## Phase 4 — Fix the dominant cause: Npgsql connection-acquisition timeouts (data-attributed 2026-06-24)

The capture phases delivered the first hard evidence. **Sentry HURRAH-TV-3** (prod, 10 events 2026-06-20 → 06-23):
`Npgsql.NpgsqlException: The operation has timed out` (inner `System.TimeoutException`). Stack:

```
DbService.OpenAsync (DbService.cs:1141)
  → NpgsqlConnection.OpenAsync → PoolingDataSource.Get → OpenNewConnector → ConnectAsync → (timeout)
```

The timeout is in **opening a physical connection**, not running a query. The event's transaction is
**`GET /api/queue`** — the call Home awaits on first paint (`Home.razor:378`) — so it presents as a slow page load.
The ~15 s symptom == Npgsql's **default connection `Timeout` of 15 s**.

**Mechanism:** `OpenAsync` (DbService.cs:1138-1143) does `new NpgsqlConnection(_connectionString)` per call (pools by
connection string — all 46 call sites dispose via `using`, **no leak**). But the connection string sets **no pool
params**, so **Minimum Pool Size = 0**: the pool drains to zero when idle, and the first post-idle request must build
a fresh connection (TCP + SSL + auth to Azure Postgres). When establishing it is slow — **4-A found the DB is in a
different Azure region (East US vs East US 2)** so each cold connect pays cross-region multi-RTT TLS, **amplified by
App Service memory-pressure GC stalls** (S1 at ~88–98%) — that handshake exceeds the 15 s timeout and the user eats a
10–15 s `/api/queue` wait. (4-A ruled out Postgres compute: CPU credits full, 0 failed connections.)

### Phase 4-A — Confirm attribution (read-only) ✅ DONE 2026-06-24
Per `Learnings/verify-plan-premise-against-live-data.md`, locked the cause before changing anything.

**Results (Azure CLI + Sentry, 06-23 metrics):**
- **Confirmed connect-time, not query-time** — Sentry stack times out in `OpenAsync → OpenNewConnector → ConnectAsync`.
- **Postgres compute RULED OUT (no tier bump):** `hurrahtv-pg` = `Standard_B1ms` Burstable, but `cpu_credits_remaining`
  flat at 292 (full) all day, `cpu_percent` 8–25%, `connections_failed` = **0**, `active_connections` peak 31 of
  `max_connections` 50. The Burstable-throttle hypothesis is dead.
- **Found — cross-region App↔DB:** App Service is in **East US**, Postgres in **East US 2**. Every connection
  establishment pays cross-region multi-RTT TLS latency; cold connects (Min Pool Size 0) are most exposed.
- **Found — App Service memory pressure:** plan `ASP-hurrahweb-8ec4` (S1, 1.75 GB, 1 worker) ran **mean 87.5% /
  peak 98% memory** on 06-23 (CPU mean 14%). Sustained pressure → GC/thread stalls can extend an in-flight connect
  past 15 s — explains the intermittency (cold connect × stall coincidence). Matches the event's 88% memory tag.
- **Attributed cause:** cold pool × cross-region connect × App-Service resource stalls — *not* PG compute.

### Phase 4-B — Fix: a warm, centrally-configured pool (main change) ✅ CODE DONE 2026-06-24
**Shipped in code** (`Program.cs` data-source registration; `DbService.cs` ctor + `OpenAsync` rent from it).
Pool params: `MinPoolSize=3, MaxPoolSize=40, KeepAlive=30, ConnectionIdleLifetime=300, Timeout=15, CommandTimeout=30`.
Verified local: build clean (0 warn), `dotnet format --verify-no-changes` exit 0, all 215 tests pass (48 Api.Tests
exercise `DbService` through the new data source against real Postgres). Remaining: PR → deploy → monitoring window.
1. **Register a single `NpgsqlDataSource` in DI** — `Program.cs` near `AddSingleton<DbService>()` (line 17), built
   from `ConnectionStrings:Default` via `NpgsqlDataSourceBuilder` (pool params in code; host/user/pass stay in the
   Azure app-setting connection string). Register as singleton.
2. **Refactor `DbService`** — inject `NpgsqlDataSource` (replace the raw `_connectionString` field); change
   `OpenAsync` to `await _dataSource.OpenConnectionAsync(cancellationToken)`. Call sites keep `using` + passed CT
   unchanged; preserves the #127 cancellation behavior.
3. **Pool-warmth + resilience params:** `Minimum Pool Size` = 2–5 (the direct fix — no cold connect after idle);
   `Keepalive` ~30 s + `Connection Idle Lifetime` ~300 s (survive Azure gateway idle timeout); deliberate connection
   `Timeout` + Dapper `Command Timeout` (fail fast vs hang); `Maximum Pool Size` **< 50** (server `max_connections`).
4. **Infra follow-ons surfaced by 4-A (decision = owner, ties to #16) — NOT a PG tier bump (compute is idle):**
   - **Co-locate App Service + Postgres in one region** — currently East US vs East US 2; cross-region connect tax
     on every cold connection. Bigger move (recreate PG in East US, or move the app to East US 2).
   - **Right-size App Service memory** — S1 is saturated (mean 87.5%/peak 98%); a bump (S2/P-series) or a memory
     investigation removes the GC-stall amplifier. Independently improves load reliability.

**Tests:** none new (DI/infra wiring, no new `HurrahTv.Shared` logic per CLAUDE.md). Gate: `HurrahTv.Api.Tests`
(real-Postgres `WebApplicationFactory`) stays green — they exercise `DbService` through the new data source.

### Phase 4-C — Complementary cold-start mitigations (optional, only if residual)
- **Post-deploy warm-up ping:** CI hits `/health` after deploy/swap so the first user isn't cold (also primes
  Min-Pool-Size connections). Ties into #16.
- **Move `DbService.InitializeAsync` DDL off the request hot path** (run-once guard / `IHostedService`); keep
  migrations expand/contract per `Learnings/shared-db-slot-swaps-need-backward-compatible-migrations.md`.

### Out of scope / follow-on
- **WASM boot failures** (Sentry HURRAH-TV-4/5/6) — separate smaller client thread; note on #200, don't fold in.
- Home client-side load levers (#175/#3) live in `Plans/home-load-and-wasm-bundle-perf.md` — don't mix.

**Verify:** local — run API with `Minimum Pool Size` set, confirm warm idle connections persist (`pg_stat_activity`);
`dotnet test HurrahTv.slnx`; `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` before
push. **Prod (real AC):** App Insights connect-time stays low + **no new HURRAH-TV-3 events** over a monitoring
window → "sporadic slow loads no longer reproduce." PR: `Fixes HURRAH-TV-3` + `Closes #200` once the window is clean.

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
