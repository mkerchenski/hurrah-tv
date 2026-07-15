# Frugal concurrency squeeze: kill connect-timeouts, hold ~5 users - Implementation Plan

> **Status:** Draft
> **Tracking issue:** mkerchenski/hurrah-tv#234
> **Paid follow-on (deferred):** #241 (GP Postgres + PgBouncer + P1v3 + region — act when concurrency actually climbs)

## Context

Npgsql connect-timeouts occur with only **1 active user** — the first rung of a concurrency ladder. This plan uses **$0 code/config levers** to eliminate the current timeouts and squeeze the existing hardware toward serving **~5 concurrent users acceptably**. The paid capacity jump needed to serve 5–10 *well* (General-Purpose Postgres + PgBouncer, P1v3 App Service, region co-location) is tracked separately in **#241** and deliberately out of scope here.

**Ground truth (verified 2026-07-15):**
- The warm pool already shipped (#222): `Program.cs:26` registers a singleton `NpgsqlDataSource` — `MinPoolSize=3, MaxPoolSize=20, KeepAlive=30, ConnectionIdleLifetime=300`, connect `Timeout`=15s default. `DbService.OpenAsync` rents from it. **The code hardcodes the pool sizes after parsing the connection string**, so a connection-string override is clobbered — per-slot tuning needs config, not just an Azure setting.
- **Postgres `Standard_B1ms`** (Burstable, 1 vCore/2 GB), `max_connections=50` **shared by prod + staging slots**; no PgBouncer on this tier.
- **App Service `S1`** (1 core/1.75 GB, ~88% memory); `IMemoryCache` is unbounded (no `SizeLimit`, no per-entry `Size`).
- Today's timeouts fire in `PoolingDataSource.OpenNewConnector` at 15s — a cold cross-region connect, forced because Home's first paint fans out **~4–6 concurrent API requests** (`Home.razor`: services → settings + queue in parallel → `/hero` → for-you/new rows) while only **3** connectors are warm.

**Intended outcome:** zero `Npgsql ... operation has timed out` events at current load, and a load-tested ~5-concurrent ceiling on today's hardware — with a clear trigger to escalate to #241.

## Affected Projects

| Project          | Touched | Notes                                                                          |
|------------------|---------|--------------------------------------------------------------------------------|
| HurrahTv.Api     | yes     | `Program.cs` (pool params from config, memory-cache `SizeLimit`); `DbService.cs` (combine Home-path queries); `TmdbService.cs` + `CurationEndpoints.cs` (per-entry cache `Size`) |
| HurrahTv.Client  | no      | —                                                                              |
| HurrahTv.Shared  | no      | no DTO/pure-logic change → no new Shared tests required                        |

**DB schema:** no changes.

---

## Phase 1 — Size the warm pool to the Home fan-out (code, $0)

- **`Program.cs:30`** — read `MinPoolSize` / `MaxPoolSize` from configuration (defaults `MinPoolSize=8`, `MaxPoolSize=20`) instead of hardcoding, so both slots pick up defaults and staging can override in Phase 2. Raising the warm floor 3→8 keeps the whole Home burst on warm connectors, removing the hot-path cold opens that time out today.
- **Do NOT raise `MaxPoolSize`** past 20 — with two slots sharing `max_connections=50`, `20 + 20 + Azure overhead` must stay < 50. Leave connect `Timeout`/`CommandTimeout` at defaults (raising `Timeout` just trades a fast error for a slow hang).
- **Tests:** no new Shared logic. Confirm `HurrahTv.Api.Tests` green and the raised `MinPoolSize` stays under the local/CI Postgres `max_connections`.
- **Verify:** cold-start the API, load Home, confirm via `pg_stat_activity` that the first-paint requests are served with no new-connector open on the hot path; ≥8 connectors idle-persist.

## Phase 2 — Isolate staging's connection budget (config, $0)

Staging auto-deploys on every `main` push and holds warm connectors + bursts against the *same* 50-cap, stealing prod's headroom.

- Set **slot-sticky app settings on the staging slot only** to a tiny pool (`Npgsql__MinPoolSize=1`, `Npgsql__MaxPoolSize=5`) via the config keys added in Phase 1. Mark them **deployment-slot settings** (sticky) so a swap doesn't carry them to prod.
- Net effect on the 50 budget: prod `8→20` + staging `1→5` + Azure overhead stays comfortably clear, giving prod the burst room.
- **Verify:** after a staging deploy, `pg_stat_activity` shows staging holding ≤5 connections; prod unaffected across a swap.

## Phase 3 — Cut the Home first-paint DB round-trips (code, $0, highest efficiency lever)

Each request opens **several sequential connections** (one per `DbService` method) — churn that compounds under load. Target the Home first-paint cluster specifically (not a global refactor):

- Combine the `GetUserPreferencesAsync` + `GetUserServicesAsync` pair (both fire in `GetPersonalizedAsync`, the endpoint in today's Sentry stack) into a single connection / round-trip.
- Fold per-show sentiment reads into one query (#7 — `GetShowSentimentsAsync`).
- Where an endpoint calls 2+ `DbService` methods in sequence, open **one** connection and pass it through, rather than open-per-method.
- **Tests:** if any query-shaping logic lands in `HurrahTv.Shared`, add a test; otherwise `Api.Tests` regression-covers.
- **Verify:** App Insights dependency count per Home request drops; results unchanged.

## Phase 4 — Bound the memory cache (code, ≈$0 — the cap branch of #220)

The unbounded `IMemoryCache` is a prime suspect for the ~88% sustained memory that triggers the GC stalls amplifying connect-timeouts. Cap it rather than scale the box (scale-up is deferred to #241).

- Enable `MemoryCacheOptions.SizeLimit` in `Program.cs:16`. ⚠️ Prerequisite: add a per-entry `Size` to **all 13 `.Set` sites** (11 in `TmdbService.cs`, 2 in `CurationEndpoints.cs`) — without it, enabling `SizeLimit` throws `InvalidOperationException` at runtime. Coarse sizing (e.g. 1/entry) + a sane limit is enough.
- **Verify:** App Service memory sustained under ~70%; no cache exceptions in logs; cache still hits.

## Phase 5 — Verify AI path + load test the ceiling

- **Confirm no DB connection is held across an Anthropic/TMDb call** in the curation/search paths (a pinned connection during a seconds-long AI call would exhaust the pool under concurrency). The per-method `using` pattern suggests it's clean — confirm, and check the #129 curation gate serializes candidate-pool gather so concurrent users don't stampede.
- **Load test staging** (k6 / bombardier, 5 virtual users on the real Home flow). This is the pass/fail for "holds ~5": p95 acceptable, zero timeout/connection errors. Record where it breaks.

## Phase 6 — Close out + escalation gate

- Confirm #234 AC over a monitoring window: no new `Npgsql ... operation has timed out` (HURRAH-TV-2/3/7/8) events. The Sentry issues are resolved *in next release*, so they auto-regress if the family recurs — the live signal.
- Close #234; note the #220 **cap branch** is done here (scale-up deferred to #241); region (#221) deferred to #241.
- **Escalation trigger:** if the Phase-5 load test can't hold ~5 concurrent, or real sustained concurrency approaches ~5 → act on **#241** (paid capacity: GP Postgres + PgBouncer, P1v3, co-location).
- `/compound` the key learning: warm-pool `MinPoolSize` must track the client fan-out width, and a Burstable DB + shared 50-connection cap is a hard concurrency ceiling no app-side tuning can beat.

---

## API Considerations
- Pool params via `NpgsqlDataSourceBuilder` in `Program.cs`, now config-driven so staging and prod differ by slot setting — no code fork.
- `OpenAsync` keeps threading `CancellationToken` (pins #127).
- No endpoint/response-shape changes; combining queries must preserve exact result shapes.

## Follow-on
- Watch the resolved-in-next-release Sentry issues after each deploy — regression = the fix didn't hold.
- When #241 is actioned, revisit `MaxPoolSize` upward (PgBouncer + higher `max_connections` remove today's constraint).
