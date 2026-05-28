# Verify a Plan's Central Premise Against Live Data Before Building Infrastructure

> **Area:** Workflow | Design
> **Date:** 2026-05-28
> **Resolves:** mkerchenski/hurrah-tv#145

## Context

#145 reported "queue items render with no streaming-service badge despite being surfaced in Home's Available Now row." The diagnostic discussion identified two failure modes and we wrote a 5-phase plan (`Plans/available-now-backfill-and-watching-override.md`):

1. Shared changes — `BackfillPending` DTO field, `IsOnService` predicate, Watching override
2. Bounded synchronous provider backfill on `/api/queue` (2s ceiling, race-then-fallback)
3. Episode-data freshness refresh for Watching items
4. Skeleton renderer in `WatchlistRow.razor` for `BackfillPending == true`
5. Queue chip-filter alignment via `IsOnService`

All 5 phases shipped on a feature branch (4 commits, ~270 lines added). The work passed tests and formatter. Then the user ran the app, looked at the actual UI, asked for the dev-DB row, and we discovered Jimmy Kimmel Live's `availableonjson` was `"[]"` *even after a fresh backfill* — TMDb genuinely has nothing for talk shows (see [[tmdb-providers-empty]]). The backfill machinery in Phase 2/3 was refreshing data that TMDb couldn't supply; the skeleton in Phase 4 almost never fired because the backfill *did* complete (with empty); the predicate alignment in Phase 5 was code-quality only with no behavior change.

We reverted phases 2-5 to a single commit (the Watching override from Phase 1, the only piece that actually changed user-visible behavior for Kimmel-class shows). The reverted code was never wrong; it just didn't pay back for the specific problem it was designed to solve.

## Learning

A plan's central premise is the *unstated assumption that makes the design pay back*. For #145 that premise was: **"the data is stale and we can refresh it."** The plan was sound IF that premise held. It didn't.

Before building infrastructure around an assumption about external data, verify the assumption against live data — directly, in 30 seconds, not 5 phases of code later. Specifically:

- **If the design hinges on retry/refresh** — query the live DB or hit the live API for one canonical example. If the answer comes back empty *after* a refresh, retry isn't the lever.
- **If the design hinges on a TMDb / external-provider field being populated** — fetch one item by hand and look at the actual JSON. Don't assume the contract is fully populated.
- **If the design hinges on a particular failure mode** — find the bug surface in production data first. The DB query for #145 (`SELECT availableonjson FROM queueitems WHERE title ILIKE '%kimmel%'`) would have surfaced the empty-permanent state in under a minute and changed the whole plan's shape.

This isn't about being timid with plans — broad plans are fine when the central premise is right. It's about catching the *false* central premise before it ladders into a multi-day implementation. The premise check is cheap; the implementation isn't.

## Example

The Kimmel row from the dev DB during #145 verification:

```
 id | title              | availableonjson | availableoncheckedat
----+--------------------+-----------------+-------------------------------
 67 | Jimmy Kimmel Live! | []              | 2026-05-28 10:01:13.859756-04
```

`availableonjson = []`, `availableoncheckedat` from minutes ago. The empty bracket is the truth, not a stale-data symptom. Any plan whose value depended on backfill turning that `[]` into a non-empty list was building on sand.

What the plan should have done first:

```bash
# 1. find a canonical instance of the bug
psql -h localhost -U postgres -d HurrahTv \
  -c "SELECT availableonjson, availableoncheckedat
      FROM queueitems
      WHERE title ILIKE '%kimmel%';"

# 2. ask: does this state survive a fresh refresh?
#    If availableoncheckedat is recent and the value is still empty → the data
#    gap is intrinsic. Backfill is not the lever. Design something else.
```

Total cost: 30 seconds. Saved cost: 4 commits, ~270 lines, and a revert.

## When to apply

- Before writing a `Plans/` document where any phase depends on an external-data refresh, freshness, or transformation.
- Before promising "we'll catch this in a backfill" in a design discussion.
- After `/xplan` produces a multi-phase plan — the verification belongs at the top of Phase 1, not implicitly inside Phase N's "verify" step.

## Anti-pattern

Designing a multi-phase plan, implementing it, *then* verifying. The implementation cost is sunk and the temptation to keep what was built (rather than admit the premise was wrong) is real. Verify first; build second.
