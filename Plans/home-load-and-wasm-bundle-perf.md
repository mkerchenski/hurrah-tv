# Home Load Speed (#175) + WASM Bundle Reduction (#3) — Implementation Plan

> **Status:** Active — #3 shipped (`3e67b40`); #175 Phase 4 planned (refetch-on-interaction + eager LCP), approved 2026-06-16
> **Phase:** #3 done; #175 Phase 4 ready to implement (4a measure → 4b refetch fix → 4c eager LCP)
> **Tracking issues:** mkerchenski/hurrah-tv#175, mkerchenski/hurrah-tv#3
> **Branch:** `perf/home-load-and-wasm-bundle-175-3`

## Decisions & results (2026-06-15)

| Lever | Outcome | Evidence |
|---|---|---|
| `wasm-tools` relinking | **kept** | enables native trim; CI needs the workload install step |
| Globalization shard (`BlazorWebAssemblyLoadAllGlobalizationData=false`) | **kept** | EFIGS user fetches one ICU shard; culture/date formatting preserved (did NOT set InvariantGlobalization/Timezone) |
| Feature switches (DebuggerSupport/MetadataUpdater/EventSource/HttpActivity off, UseSystemResourceKeys) | **kept**, Release-only | lets trimmer strip more; dev hot-reload/debug intact |
| **AOT** (`RunAOTCompilation`) | **DROPPED** | +2.4 MB download for **no measurable boot win**: 539–576 ms (AOT) vs 518–623 ms (non-AOT) on Release landing — ranges overlap. Startup is render-bound, not managed-compute-bound. Also slows CI. |
| **JWT custom-decoder swap** | **DROPPED** | its only benefit was ~36 KB of download; under "PWA → download isn't the priority" that's noise, and keeping the battle-tested `System.IdentityModel` library is safer than a hand-rolled decoder for auth. |

**Final bundle (EFIGS user, brotli):** 2.90 MB baseline → **2.46 MB** (~15% smaller). csproj is the only code change.

**Big #175 reframing:** Release landing boots in **~550 ms**; the alarming 1.48 s came from the **dev server shipping the untrimmed BCL** (238 assemblies vs 55 in Release). So most of #175's "slow load" is a dev-only artifact. **Still TODO:** re-measure *authenticated* Home on a Release build to confirm #175 is acceptable; apply the cheap eager LCP-image-discovery tweak if the poster still lands late.

## Phase 1 findings (2026-06-15) — these reordered the work

Measured Release bundle + authenticated Home load (latter via Chrome DevTools, dev server, logged-in session):

- **Bundle (Release publish):** 2.9 MB brotli, 55 assemblies. Runtime dominates (`dotnet.native.wasm` 2.9 MB + `System.Private.CoreLib` 1.67 MB raw). Full `System.IdentityModel.*` + `Microsoft.IdentityModel.*` tree = 5 assemblies, ~36 KB brotli (#3 JWT-swap target). **`wasm-tools` workload not installed** → publish runs "without optimizations" (no WASM relinking/AOT).
- **API is NOT the bottleneck:** `/api/queue` = **31–36 ms**, hero = 38 ms, all API calls < 120 ms.
- **Authenticated Home LCP = 1.48 s**, breakdown = **1,044 ms load-delay + 401 ms render-delay, ~0 network**. The cost is **WASM boot + first render**, not the data path. LCP poster image discovered late (`LCPDiscovery` insight).
- **Caveat:** these LCP numbers are from `dotnet watch` dev, which ships the *untrimmed* BCL (238 assembly requests vs 55 in Release) → dev boot is inflated. A true prod-boot number needs a Release serve.

**Consequence:** #175's premise ("decouple refresh from first paint") targets an already-cheap path (refresh is fire-and-forget; queue=35 ms). The real lever for perceived initial load is **WASM boot = the #3 bundle work**. So **do #3 first (Phase 2→3), then re-measure Home against a Release build** before deciding whether #175 needs any further client-side work (Phase 4). Cheap boot-independent #175 win still on the table: eager LCP-image discovery.

## Context

Two perf issues on the **v1 Public Launch** milestone, both load-time, both measurement-first per their acceptance criteria. Bundled into one branch because they share the same surface (WASM boot + Home first paint) and the same baseline measurement.

- **#175** — Home initial load feels sluggish; it's the most-hit interaction in the app.
- **#3** — WASM bundle has no trimming config and ships `System.IdentityModel.Tokens.Jwt` (a full token-signing library) to do a client-side claims decode.

**Key findings from pre-research** (these shape the plan):

- **#175 is already partly handled server-side.** `/api/queue` (`QueueEndpoints.cs:45-50`) fire-and-forgets the TMDb stale refresh, so the response does **not** block on TMDb. First paint client-side blocks on `await queueTask` at `Home.razor:357`. The AI hero is already fire-and-forget (`Home.razor:361`) and not on the paint path. So the remaining #175 cost is some mix of **(a) WASM boot, (b) the `/api/queue` DB round-trip, (c) no client-side queue cache** — every status-chip toggle, media-filter change, and quick-action completion refetches the entire queue. Phase 1 measurement decides which dominates.
  - Note the tension with `Learnings/api-await-with-timeout.md`: a bounded `WaitAsync` was once used so daily episode badges weren't stale on first load; the current code is fire-and-forget again. Any #175 change to the refresh path must not silently reintroduce the "stale until second reload" bug that learning documents.
- **#3 JWT swap is low-risk.** `System.IdentityModel.Tokens.Jwt` (8.18.0) is used in exactly one file (`HurrahAuthStateProvider.cs`) and only to read `exp` + claims — no signature verification (the API does that). The client consumes only three claims, all flat strings: `exp` (`HurrahAuthStateProvider.cs:24`), `firstname` (`Home.razor:338`, `MainLayout`), and `is_admin == "true"` (`MainLayout.razor:163` + the client `"Admin"` policy gating six admin pages). Server mints them as plain string claims (`AuthService.cs:44-48`). No roles array, no claim-type mapping relied upon → a tiny custom decoder reproduces today's behavior exactly.

**Intended outcome:** measurably faster Home first paint, a smaller `_framework` payload, no auth/admin regressions.

## Affected Projects

| Project          | Touched | Notes                                                                                  |
|------------------|---------|----------------------------------------------------------------------------------------|
| HurrahTv.Api     | maybe   | only if Phase 1 data points at the `/api/queue` round-trip as the bottleneck           |
| HurrahTv.Client  | yes     | `HurrahTv.Client.csproj` (trim), `HurrahAuthStateProvider.cs` (JWT swap), Home load path |
| HurrahTv.Shared  | yes     | new pure `JwtPayload` decoder utility (testable) — no DTO/contract change               |

**DB schema changes:** none. (No `DbService.InitializeAsync` migration this branch.)
**API contract changes:** none. (No Shared DTO ripple.)

---

## Phase 1 — Baseline measurement (both #175 + #3) — STOP for review

No code. Capture and document the baseline in **both** GitHub issues, then pause: per your call, you review the #175 numbers and pick the direction before any #175 code lands.

- **Client load waterfall** (cold + warm) via Chrome DevTools MCP against the running dev site: WASM boot/TTI/LCP, `/api/queue` timing, and the refetch cost on chip-toggle / media-filter / quick-action.
- **Bundle baseline:** `dotnet publish -c Release HurrahTv.Client` → total `_framework/*.{wasm,dll}` size (and brotli sizes). Record the `System.IdentityModel.*` / `Microsoft.IdentityModel.*` contribution specifically.
- **Lighthouse** performance pass for a TTI/LCP number to compare against post-change.

**Output:** baseline comment on #175 (waterfall + where the time goes) and on #3 (sizes + TTI). Decide #175 approach with you here. Candidate directions: client-side queue cache (stale-while-revalidate) to kill refetch-on-interaction; deferred refresh-then-update; trim the `OnInitializedAsync` await chain. **Tests:** none (measurement only).

---

## Phase 2 — #3: `wasm-tools` AOT/relinking + trimming + drop globalization data

`HurrahTv.Client/HurrahTv.Client.csproj` + build toolchain. Independently committable, no interaction with #175.

**Highest-leverage item (folded in from Phase 1 findings):** the Release publish logs *"Publishing without optimizations… strongly recommend `wasm-tools`."* The runtime dominates the bundle — `dotnet.native.wasm` (2.9 MB raw) + `System.Private.CoreLib` (1.67 MB raw) — and only the `wasm-tools` workload unlocks WASM relinking/native trimming of it. Bigger lever than the JWT swap and managed-trim flags combined.

- `dotnet workload install wasm-tools` locally; add the same install step to CI (`.github/workflows/main_hurrahtv.yml`) so the staging/prod Release publish gets the same optimization. **CI-toolchain change — call out in the PR.**
- Enable WASM relinking; evaluate `<RunAOTCompilation>true</RunAOTCompilation>` (trades a larger download for faster runtime — measure both download size *and* TTI; AOT can grow the payload while speeding execution, so pick per the numbers, don't assume).
- Add `<BlazorWebAssemblyLoadAllGlobalizationData>false</BlazorWebAssemblyLoadAllGlobalizationData>` (app has no i18n need).
- Managed trimming is already on for Release (publish logs "Optimizing assemblies for size") — this is about *tightening* it: evaluate `<TrimMode>full</TrimMode>` only if partial stays clean.
- Review/triage trim warnings (System.Text.Json reflection paths are the usual suspect — `Learnings/blazor-wasm-static-mutable-arrays.md`, `Learnings/blazor-wasm-threading-model.md`).
- Re-measure `_framework` brotli size **and** TTI vs Phase 1; record before/after in the #3 PR comment.

**Verify:** `dotnet publish -c Release` with `wasm-tools` present; confirm app boots and auth + API calls work; confirm the CI publish still succeeds with the workload step. **Tests:** none (build config); the gate is "zero unresolved trim warnings + app works + download and/or TTI improved."

---

## Phase 3 — #3: custom JWT payload decoder in Shared, drop the package

Replace `System.IdentityModel.Tokens.Jwt` so its whole dependency tree leaves the bundle.

- **New** `HurrahTv.Shared/Auth/JwtPayload.cs` — pure helper: split on `.`, base64url-decode the payload segment (handle missing padding), `JsonDocument.Parse`, enumerate **every** property into `Claim(name, value)`, expose `exp` as a `DateTimeOffset`. No signature work.
- `HurrahAuthStateProvider.cs` — swap `JwtSecurityTokenHandler.ReadJwtToken` for the helper; build `ClaimsIdentity` from the decoded claims; keep the existing expiry + clear-on-failure behavior.
- Remove the `System.IdentityModel.Tokens.Jwt` `PackageReference` from the csproj.
- Re-measure bundle vs Phase 2.

**Tests (REQUIRED — pure Shared logic per CLAUDE.md):** `HurrahTv.Shared.Tests` named regression tests pinning #3:
- `Decode_Surfaces_IsAdmin_Claim` — the load-bearing one; miss `is_admin` and admins silently lose admin UI.
- `Decode_Reads_Exp_As_Expiry` and an expired-token case.
- `Decode_Handles_Base64Url_Without_Padding`.
- `Decode_Returns_Empty_On_Malformed_Token` (mirrors the catch → signed-out path).

**Verify:** `dotnet test`; then manual sign-in + **admin nav must still appear** for an admin account, normal user must not see it (custom decoder's one real risk).

---

## Phase 4 — #175: refetch-on-interaction + eager LCP (approved 2026-06-16, full sequence)

Phase 1's "client-side queue cache" framing turned out simpler than expected on a fresh code-read:
the cache already exists. `_allItems` (`Home.razor:419`) holds the full queue and
`ReapplyDerivedState()` (`Home.razor:430`) re-partitions rows/rec-seeds/new-episode-set/hero
**purely from it** — no network. Every optimistic mutation (`OnItemUpdated`, `OnItemRemoved`,
`OnEpisodeWatchedChanged`) already splices in place then `ReapplyDerivedState(); StateHasChanged()`.
But two **pure-derivation** interactions still do a full `/api/queue` refetch instead — that's the fix.

### Phase 4a — Re-measure authenticated Home on a Release build (baseline for AC)
No code. The "before" half of #175's "measurably improved vs. baseline" AC, on a **Release** build
(not `dotnet watch`, which inflates boot per the Phase 1 reframing).
- `dotnet publish -c Release HurrahTv.Client`, serve it, sign in as a real user.
- Chrome DevTools MCP waterfall + Lighthouse: WASM boot/TTI, **LCP element + load/render-delay split**,
  `/api/queue` timing, and confirm the chip-toggle / media-filter full refetch shows in the network panel.
- Record baseline as a comment on #175. **Tests:** none (measurement only).

### Phase 4b — Kill refetch-on-interaction (the main fix)
- **`ToggleStatusFilter`** (`Home.razor:535`) — after flipping `_settings.ShowX` and persisting via
  `SetUserSettingsAsync`, replace `await ReloadWatchlist()` with `ReapplyDerivedState(); StateHasChanged();`.
  (Settings persistence is off the render path — keep awaited or fire-and-forget like
  `OnWatchlistSortChanged` at `Home.razor:631`.) Also fix its stale "uses cached queue data" comment.
- **`OnMediaFilterChanged`** (`Home.razor:676`) — keep `_ = LoadHero()` (media-appropriate AI hero needs
  the server, #147), but replace `await ReloadWatchlist()` with `ReapplyDerivedState(); StateHasChanged();`.
  The media partition is a pure function of services + items + media type (`OnUserServicesChanged`,
  `Home.razor:698`, already does exactly this with no refetch).
- **Leave `OnQuickActionChanged` (`Home.razor:670`) as a refetch** — it fires *after a server mutation*,
  so server-fresh data is justified; granular events cover the single-item cases. Out of scope; note in PR.
- No staleness risk (`Learnings/api-await-with-timeout.md`): these are *interaction* paths over
  already-loaded data; `OnInitializedAsync` still does the real first-load fetch. Sync handlers, so no
  fire-and-forget `StateHasChanged` hazard (`Learnings/blazor-async-statehaschanged.md`).
- **Tests:** none — Blazor wiring over the existing pure helper; filter logic already covered in
  `HurrahTv.Shared.Tests`.
- **Verify:** with the network panel open, toggle each status chip + the movie/TV filter — **no
  `/api/queue` request fires**, rows update instantly; last-active-chip snap-back guard holds; hero
  re-picks on media change.

### Phase 4c — CLS fix + cheap LCP hints (RE-SCOPED by 4a measurement)
**4a finding (2026-06-16, prod `d4a2185`):** LCP = 1056 ms, **load-delay 901 ms dominant**; the LCP
element is the **AI hero backdrop `<img>`** (`HomeHero.razor:13`), whose URL isn't known until WASM
boots **and** `/api/curation/hero` returns (image not queued until 941 ms; download itself 0.6 ms).
So the original "eager LCP discovery" premise is **weak** — `preconnect`/`fetchpriority` can't fix
"discoverable in initial document" for a post-boot, AI-chosen resource, and the download is already
instant. The real LCP lever (WASM boot) was #3, already shipped. **CLS = 0.154 ("needs improvement")
surfaced as the more valuable perceived-quality win.**

- **CLS 0.154 (new headline 4c work)** — worst cluster 662–1715 ms, dominated by a 0.12 shift as
  post-boot content renders. Likely the **hero skeleton→content swap and/or unreserved watchlist row
  height**. Reserve fixed height for the hero billboard + skeleton and for watchlist rows so swaps
  don't reflow. Cf. `Learnings/blazor-set-loading-flag-before-derived-state.md`. Verify CLS < 0.1.
- **Cheap LCP hints (keep — harmless, low impact):** add
  `<link rel="preconnect" href="https://image.tmdb.org" crossorigin>` to `index.html` and
  `fetchpriority="high"` on the hero `<img>` (`HomeHero.razor:13`). Don't expect a big LCP move.
- **Double `/api/queue` on load (new — investigate):** 4a saw two `GET /api/queue` during initial
  boot (reqid 423 + 427). Trace the second fetch (likely an `OnUserServicesChanged` / re-init path);
  drop it if redundant — a boot-path win independent of 4b.
- **Tests:** none (markup/CLS). **Verify:** re-run the 4a trace on the shipped build; CLS < 0.1,
  no LCP regression; document after-numbers on #175.

**Follow-on (out of scope):** converge `OnQuickActionChanged` onto the granular splice-in-place path
to drop its post-action refetch too — candidate future issue.

---

## Blazor WASM Considerations

- `HurrahAuthStateProvider` is the auth-state source for routing + `<AuthorizeView>`; the decoder swap must yield the same `ClaimsPrincipal` shape or admin gating breaks.
- Any Phase 4 background revalidation must re-render via the established pattern (not bare fire-and-forget `StateHasChanged`) and respect the page-scoped `_cts` / `_disposed` guards already in `Home.razor`.
- Trimming can break reflection-based JSON — be ready to add `JsonSerializerContext` / `[DynamicallyAccessedMembers]` if Phase 2 surfaces warnings.

## Verification (end-to-end)

- Launch the dev site in a visible Ghostty tab (per workflow — do not restart the app silently; ask Mike).
- Chrome DevTools MCP for the before/after waterfall + Lighthouse; `dotnet publish -c Release` for bundle sizes.
- `dotnet test` (Shared decoder tests) and manual admin-nav check after Phase 3.
- **Before push:** `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` (the exact CI command).

## Follow-on

- After landing: `/compound` to capture any trim-warning or decoder gotchas.
- PR description closes both: `Closes #175` / `Closes #3`.
