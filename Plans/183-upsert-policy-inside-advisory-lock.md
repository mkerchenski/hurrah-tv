# Cover UpsertWithStatusAsync's race-lost mutation with the advisory lock (#183) - Implementation Plan

> **Status:** Draft
> **Phase:** 1 (single phase — one file)
> **Tracking issue:** mkerchenski/hurrah-tv#183

## Context

`UpsertWithStatusAsync` in `HurrahTv.Api/Services/DbService.cs` has two paths that both call
the shared local function `ApplyUpdatePolicy` (status transition + backdrop backfill):

- **Fast path** (line ~446): `SELECT` finds the row → `ApplyUpdatePolicy(existing)` runs single
  `UPDATE … WHERE Id = @Id` statements on the connection in autocommit (no transaction).
- **Race-lost path** (line ~484): the per-user advisory-locked INSERT (`pins #163`) hit
  `ON CONFLICT DO NOTHING`, so it re-reads the row **inside** the transaction, calls
  `tx.CommitAsync()` — which **releases the advisory lock** — and only **then** runs
  `ApplyUpdatePolicy(existing)`. The policy's UPDATE therefore lands outside both the transaction
  and the lock the flow implies.

**Benign today** (per #183): every policy sets an *absolute* value keyed by primary `Id` and is
idempotent (two concurrent `/seen` both set `Status=Finished`), so there's no divergence. The
concern is structural — the advisory lock (`#163`, see
`Learnings/postgres-advisory-lock-key-hashtextextended.md`) covers the `MAX(Position)+INSERT` but
not the mutation that immediately follows, which would bite a future *non-idempotent* / relative-update
policy.

**Decision (approved):** *Hybrid* — move the **race-lost path's** `ApplyUpdatePolicy` inside the
held tx/lock (near-zero cost; we're already in the transaction), and **document** the fast path as
safe (single-row, by-Id, atomic) plus pin the idempotent-by-Id invariant for future policy authors.
Do **not** add a lock to the fast path (it's the hottest add-path and gains nothing today).

**Why `tx` threading is mandatory, not polish:** Npgsql throws if a command runs on a connection
with an open transaction without enlisting that transaction. The race-lost path runs
`ApplyUpdatePolicy` *after* commit today precisely because the policy's `db.ExecuteAsync` calls
don't pass `tx`. To run it *before* commit, the `tx` must be threaded through — otherwise it throws.

**Intended outcome:** the race-lost mutation commits atomically under the advisory lock; the fast
path is unchanged but documented; the safety invariant is explicit at the call sites.

## Affected Projects

| Project          | Touched | Notes                                                                 |
|------------------|---------|-----------------------------------------------------------------------|
| HurrahTv.Api     | yes     | `Services/DbService.cs` — `UpsertWithStatusAsync` / `ApplyUpdatePolicy` |
| HurrahTv.Client  | no      | —                                                                     |
| HurrahTv.Shared  | no      | no DTO change                                                         |
| HurrahTv.Api.Tests | yes   | behavior-preservation regression test (real Postgres)                 |

## DB Schema Changes

None. No `DbService.InitializeAsync` change, no migration. Pure control-flow / transaction-scope change.

## Change (all in `HurrahTv.Api/Services/DbService.cs`)

1. **Thread `tx` through `ApplyUpdatePolicy`** — add `NpgsqlTransaction? tx = null` to its signature
   (mirrors `SelectQueueItemAsync` at line ~1124, which already uses this exact optional-tx pattern),
   and pass `tx` to all three `db.ExecuteAsync(...)` calls inside it (the `Status+Backdrop`,
   `Status`-only, and `Backdrop`-only UPDATEs).

2. **Race-lost path (line ~484–486)** — run the policy *before* commit, enlisted in the tx, so it's
   covered by the still-held advisory lock:
   ```csharp
   existing = await SelectQueueItemAsync(db, userId, tmdbId, mediaType, tx);
   QueueItem? result = existing is null ? null : await ApplyUpdatePolicy(existing, tx);
   await tx.CommitAsync();
   return result;
   ```

3. **Fast path (line ~446)** — unchanged call (`ApplyUpdatePolicy(existing)`, `tx` defaults null →
   autocommit, identical behavior). Add a comment documenting why it's safe without a lock:
   single-row `UPDATE … WHERE Id` is atomic in Postgres, no `Position` allocation, no MAX-read race.

4. **Invariant comment** — at `ApplyUpdatePolicy`'s definition, pin the constraint: *policies must set
   absolute values keyed by `Id` and be idempotent; a future read-modify-write or relative-update
   policy MUST run inside the tx/lock on both paths.* This is the documentation half of the hybrid.

## Tests (HurrahTv.Api.Tests — real Postgres)

- **Behavior-preservation regression** (new test in `QueueEndpointsTests.cs`, referencing #183):
  drive the policy through the endpoints sequentially — add an item as `WantToWatch`, call `/seen`,
  assert `Status == Finished`; assert `EnsureQueueItem` on an existing row does **not** change status;
  assert backdrop backfill applies when the existing row's `BackdropPath` was empty. This pins that
  the `tx`-threading + reorder didn't break the policy (the real failure mode is a forgotten `tx`
  arg → Npgsql throw, which this catches the moment the race-lost path is exercised).
- **Race-lost path is not deterministically unit-testable** (it needs a real INSERT/INSERT collision).
  The existing #163 concurrency test in `QueueEndpointsTests.cs` exercises concurrent same-user adds
  and provides incidental coverage; do not add a flaky timing-based test. This is acceptable per
  CLAUDE.md — this is Api/DB logic (real-Postgres integration), not pure `HurrahTv.Shared` logic.

## Verification

1. `dotnet build HurrahTv.slnx --verbosity minimal` — confirm it compiles.
2. `dotnet test HurrahTv.slnx` — new regression test + existing #163 concurrency test pass.
   (Stop any running `dotnet watch` first — see `Learnings/dotnet-watch-locks-shared-dll.md`.)
3. Manual smoke (optional, browser): from a browse surface, `/seen` an item already in the queue and
   confirm it flips to Finished with no error; add a brand-new item and confirm it lands at the
   bottom (Position) as before.
4. `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` before push.

## Follow-on

- After landing, `/compound` a learning: *"An advisory lock only covers what executes before
  `CommitAsync`; a shared post-read mutation helper must accept and enlist the caller's `tx` or it
  silently runs outside the lock."* Links to `[[postgres-advisory-lock-key-hashtextextended]]` and
  `[[addtoqueue-conflict-pitfall]]`.
