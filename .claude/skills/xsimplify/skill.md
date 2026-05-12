---
name: xsimplify
description: Project-aware simplify for Hurrah.tv. Delegates to the system `simplify` skill for the standard pass, then layers a hurrah-tv "promote to Shared" pass that surfaces code that could move from Api/Client into HurrahTv.Shared (where it can be reused on both sides of the wire).
argument-hint: (no arguments)
---

# xsimplify — Simplify with HurrahTv.Shared Awareness

Wraps the system `simplify` skill with one extra hurrah-tv-specific pass: "should this code live in HurrahTv.Shared so both Api and Client benefit?"

## Workflow

### Step 1 — Run the system simplify pass

Invoke the system simplify skill on the same diff scope as `/xreview`:

```
Skill(skill="simplify")
```

Let it complete its full review-and-fix workflow on the recently-changed code before continuing. This skill adds an elevation lens on top — it does not replace simplify's reuse / quality / efficiency review.

### Step 2 — Determine the diff scope

Same as `/xreview` — branch-agnostic so it works on main too:

```bash
git diff origin/main..HEAD     # committed-but-not-yet-on-origin
git diff HEAD                  # working tree (unstaged + staged)
```

Union of those two = the elevation surface. If both empty → "No changes to elevate" and stop.

### Step 3 — Scan relevant learnings

Before deciding on elevation, glob `Learnings/**/*.md` for any past discoveries that bear on what should or shouldn't live in Shared. Read anything related to DTO contracts, JSON serialization, WASM datetime handling, or app-specific patterns that turned out to be deliberately non-shared. If a learning warns against promoting a shape, respect it.

### Step 4 — HurrahTv.Shared elevation pass

For each piece of changed code in `HurrahTv.Api/` or `HurrahTv.Client/`, ask:

> "Is this server-only or client-only behavior, or a shape both sides need to agree on?"

Promote to **`HurrahTv.Shared/`** when the answer is "both sides need to agree." The Shared project is the contract layer — DTOs, enums, constants, pure helpers that don't depend on platform APIs.

Subfolder placement matches the existing organization in `HurrahTv.Shared/Models/`:

| Code shape                                         | Place in                                                                  |
| -------------------------------------------------- | ------------------------------------------------------------------------- |
| Request/response DTOs                              | `Models/<Area>Models.cs` (e.g. `AuthModels.cs`, `AdminModels.cs`)         |
| Enums shared by Api + Client                       | `Models/QueueItem.cs` neighborhood, or a dedicated file                   |
| Constants (status codes, well-known IDs)           | `Models/Constants.cs`                                                     |
| Provider/genre lookup tables                       | `Models/StreamingService.cs`, `Models/Genre.cs` patterns                  |
| Pure helpers (no I/O, no DI, no platform calls)    | A new file in `Models/` or extend an existing one                         |
| TmdbImage / URL helpers                            | `Models/SearchResult.cs` neighborhood                                     |

**Anti-elevation signals** (keep out of Shared):

- Depends on `HttpClient`, `IJSRuntime`, `IJSInProcessRuntime` (Client-only)
- Depends on `Dapper`, `Npgsql`, `IConfiguration`, `ILogger<T>` (server-only)
- Depends on `Microsoft.AspNetCore.*` types (Api-only)
- Depends on `Microsoft.AspNetCore.Components.*` types (Client-only)
- Captures HTTP request state or DB connection state
- Single call-site with no plausible second consumer on the other side of the wire

### Step 5 — Apply hurrah-tv-specific constraints

- **Shared is a netstandard-friendly contract layer.** Both Api (.NET 10 / ASP.NET) and Client (Blazor WASM) reference it. Anything platform-specific breaks one side.
- **Public-API stability isn't critical** since Shared isn't a published NuGet — it's a project reference. Renames are fine if you update both consumers in the same commit.
- **DTO mutations cascade** — adding a property to a Shared DTO is generally safe (default value); removing or renaming requires both Api and Client to change in lockstep.
- **JSON serialization round-trips.** Anything Shared crosses the wire as JSON; nullable fields, default values, and enum representations all matter. Avoid `ref` / `out` parameters in DTOs.
- **No DI in Shared.** Shared types are POCOs / static helpers; if you need a service, it lives in the consuming project.

### Step 6 — Report findings, await confirmation

```
## HurrahTv.Shared Elevation Candidates

### 1. {short name}
**Current location:** {file:line in Api or Client}
**Suggested target:** HurrahTv.Shared/Models/{File.cs}
**Reason:** {both sides need to agree on this shape, or pure helper used in both}
**Migration shape:** {fresh extraction OR move + thin reference at original site}
**Cross-project impact:** {what Api/Client files need follow-up changes}
```

Wait for confirmation before moving anything.

If a candidate's migration shape is **"fresh extraction"** OR it touches **both Api and Client** in non-trivial ways, the elevation is multi-day work — offer to file a `type:refactor` issue on `mkerchenski/hurrah-tv` so the work is tracked across sessions. Show the user the proposed body before running:

```bash
gh issue create --repo mkerchenski/hurrah-tv \
  --title "Promote <component> from <Api|Client> to HurrahTv.Shared" \
  --body "## What
<one-line summary lifted from the candidate>

## Why
Both sides of the wire need to agree on this shape (or pure helper used in both).

## Migration shape
<fresh extraction | move + thin reference>

## Acceptance criteria
- [ ] Code moved to \`HurrahTv.Shared/Models/<file>\`
- [ ] Original Api/Client call sites updated (or thin forwarder kept)
- [ ] Build clean across all three projects
- [ ] No platform-specific dependencies leaked into Shared

Surfaced by: /xsimplify on $(date +%Y-%m-%d)" \
  --label "type:refactor,area:<api or client>,phase:next,difficulty:<intermediate|advanced>"
```

Single-file, in-place simplifications stay inline — they're the system simplify skill's domain, not worth an issue.

## When to use

- **After every meaningful change, before committing.** The default cadence — runs cheap, catches most simplification opportunities while the code is still warm.
- Before opening a PR (alongside `/xreview`).
- After landing a feature, as a "did we leave duplication behind?" sweep.

## Guidelines

- The system `simplify` skill (Step 1) handles within-file cleanup. Don't redo that work here.
- "Both sides need to agree" is the load-bearing test — without it, leave code where it is.
- Two consumers (one in Api, one in Client) ≈ promote. One consumer with a plausible second on the other side soon ≈ judgment call. One consumer with no plausible second ≈ leave it.
- If the elevation requires touching both Api and Client, do it as one commit — Shared changes shouldn't be half-landed.
