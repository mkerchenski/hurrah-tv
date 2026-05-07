---
name: xplan
description: Project-aware planning for Hurrah.tv. Asks for a mode (quick / plan), then either runs an inline checklist or uses Claude Code's built-in plan mode to propose a phased plan with Hurrah.tv context (Blazor WASM lifecycle, TMDb/Anthropic integration, PostgreSQL migration safety) and saves the approved result to `Plans/`.
argument-hint: <feature description>
---

# xplan — Hurrah.tv Augmented Planning

Wraps the system planning skills with Hurrah.tv-specific context. Always asks for a mode first, then dispatches.

## Workflow

### Step 0 — Mode selection

Ask:

> **Which planning mode?**
>
> - **quick** — lightweight inline checklist for single-file / single-concern tasks. No file saved, no delegation.
> - **plan** — use Claude Code's built-in plan mode to propose a phased plan with Hurrah.tv context, get your approval, then save the approved plan to `Plans/<feature>.md` with Hurrah.tv augmentation appended (DB migration safety, Blazor WASM lifecycle, TMDb/AI integration notes).
>
> **When to pick `plan`:** typical feature work that needs the structured `Plans/` artifact.
> **When to pick `quick`:** single-file, single-concern tasks.

Wait for the response before proceeding.

---

### Step 1-Quick — inline checklist

For single-file / single-concern tasks. No file saved.

```markdown
## Quick Plan: {Task Name}

**Files touched:** {list}
**Touches DB schema:** yes/no (would force a migration in `DbService.InitializeAsync`)
**Touches API contract:** yes/no (Shared DTO change ripples to Client)

- [ ] {task}
- [ ] {task}

**Verify:** {one-line check}
```

If the task turns out more complex mid-implementation, suggest switching to `plan`.

---

### Step 1-Plan — delegate, then layer Hurrah.tv context

#### 1.1 Pre-research

Run these in parallel:

1. **Scan learnings** — `Glob Learnings/**/*.md` then read any whose title bears on the feature area (Blazor lifecycle, WASM datetime, TMDb, AI curation, Postgres, etc.).
2. **Check existing plans** — `Glob Plans/*.md`. **If a plan in the same area already exists, prefer updating it over creating a new one.** Note related plans for the system plan skill to reference.
3. **Read CLAUDE.md** at the repo root for architectural constraints.
4. **Check Memory** — read relevant memory files for project context, user preferences, and past learnings.
5. **Identify affected projects** — which of `HurrahTv.Api`, `HurrahTv.Client`, `HurrahTv.Shared` does this touch?

#### 1.2 Propose via plan mode

Enter Claude Code's built-in plan mode to propose a phased plan with the pre-research findings as context.

`EnterPlanMode` and `ExitPlanMode` are deferred tools — load their schemas first:

```
ToolSearch("select:EnterPlanMode,ExitPlanMode")
```

Then call `EnterPlanMode` with a `plan` argument that includes:

- The user's task verbatim
- Pre-research findings: affected projects (Api / Client / Shared), relevant learnings, related existing plans
- An instruction that **DB schema changes are always Phase 1**, each phase is independently committable, and each ends with a Verify step
- Hurrah.tv-specific constraints to honor: schema migrations must be idempotent (`ALTER … IF NOT EXISTS`), Shared DTO changes ripple to both Api and Client, Blazor WASM render-mode and lifecycle constraints, fire-and-forget background work uses `IServiceScopeFactory`

The user reviews the proposed plan in plan mode and either approves, requests changes, or rejects. The skill resumes after `ExitPlanMode` is called on approval. If rejected, stop — do not save anything to disk.

#### 1.3 Layer Hurrah.tv augmentation

After plan mode exits (user approved), append these Hurrah.tv-specific sections to the approved plan before saving:

**Affected Projects** — fill in:

| Project              | Touched (yes/no) | Notes                                                       |
|----------------------|------------------|-------------------------------------------------------------|
| HurrahTv.Api         |                  | endpoints, services, DbService schema/queries               |
| HurrahTv.Client      |                  | Blazor pages, components, ApiClient                         |
| HurrahTv.Shared      |                  | DTOs — change here ripples to both Api and Client           |

**DB Schema Changes** — if the plan mutates the schema, explicitly note:

- New tables or columns added in `DbService.InitializeAsync` using `IF NOT EXISTS` (idempotent)
- Backfill path for existing rows (migration runs on every startup; older rows may need lazy-fill on next user touch)
- Bootstrap data — any rows seeded during init (e.g. `Admin:BootstrapPhones`)

**Blazor WASM Considerations** — if the plan affects pages or components, flag:

- Component lifecycle disposal (event handlers, timers, service subscriptions)
- DI scope — new services should be Scoped (Singleton consuming Scoped will fail validation)
- Optimistic UI vs server-confirmed — match existing pattern (`QuickActionService.NotifyItemUpdated`)
- Skeleton placeholders for any new async-loaded section to avoid CLS

**API Considerations**:

- Mutating endpoints return the updated entity so client can splice in place
- Background work uses `IServiceScopeFactory.CreateAsyncScope()` for fresh transients
- Authorization: `RequireAuthorization()` on all user endpoints; `RequireAuthorization("Admin")` for admin

**External integrations** — if the plan touches TMDb, Anthropic, or Twilio:

- TMDb: cache aggressively (existing TTLs in `TmdbService`); rate limits are real
- Anthropic: every call costs money — server-side `CurationCache` keyed by watchlist hash; client `CurationCache` localStorage TTL
- Twilio: only used for OTP send; phone-number normalization at Auth layer

**Follow-on actions:**

- After landing: invoke `/compound` to capture non-obvious learnings
- After AI prompt changes: clear `CurationCache` server-side or just bump the cache key

#### 1.4 Save

Save the augmented plan to `Plans/<feature-name>.md`. The approval already happened inside plan mode — this step writes the durable artifact (referenceable across sessions).

Tell the user: "Plan saved to `Plans/{feature-name}.md`. Start Phase 1 now, or revisit later?" — don't auto-start; multi-phase work often picks up in a future session.

---

## Guidelines

- **Phases should be independently committable** — each leaves the system in a working state.
- **DB schema changes are always Phase 1** — Shared DTOs (if any) follow in Phase 1 or 2.
- **One concern per phase** — don't mix schema changes with UI changes.
- **Reference learnings by filename** so they're easy to find.
- **Plan size guidelines**:
  - Small (1-2 files) → 1-2 phases → `quick` mode
  - Medium (3-10 files) → 2-4 phases → `plan` mode, save to `Plans/`
  - Large (10+ files, cross-project) → 4-6 phases → `plan` mode
- **Don't over-plan** — for trivial single-file tasks, `quick` is enough.
- **Plans/ is gitignored** — for working documents, not permanent docs.
