# Shared-DB Slot Swaps Need Backward-Compatible (Expand/Contract) Migrations

> **Area:** Infra | API
> **Date:** 2026-06-01

## Context

The staging and production App Service slots share **one** PostgreSQL database. Schema
changes live in `DbService.InitializeAsync`, which runs on every app startup. So the moment a
staging deploy boots, its `InitializeAsync` migrates the **production** database — before any
swap happens.

#161 shipped a non-backward-compatible migration: `CurationHeroImpressions` was dropped and
recreated with PK `(UserId, TmdbId, MediaType)` and `MediaType VARCHAR(10) NOT NULL` (no
default). The pre-#161 code's `RecordHeroImpressionAsync` does
`INSERT (UserId, TmdbId, ShownAt) … ON CONFLICT (UserId, TmdbId)`. Against the new schema that
fails two ways — the INSERT omits the NOT NULL `MediaType`, and `ON CONFLICT (UserId, TmdbId)`
no longer matches any constraint.

## The trap

With a shared DB + slots, **whichever slot runs the old code against the just-migrated schema
breaks.** There are two such windows:

1. **Deploy → swap gap:** staging boots new code (migrates the DB), but prod is still old code
   until the swap. Prod breaks in the meantime.
2. **After the swap:** the old code lands on the *staging* slot, which still shares the DB —
   so staging breaks until its next deploy.

A column default wouldn't have saved it: the old `ON CONFLICT` target breaks regardless of the
default, because the unique constraint it names no longer exists.

## Learning

For schema changes on the shared DB, use **expand/contract**, never a one-shot breaking change:

- **Expand (this deploy):** additive only — `ADD COLUMN … DEFAULT`, add a *new* unique index
  alongside the old one, widen types. Old code must keep working against the expanded schema.
  Don't drop columns, don't change a PK an old `ON CONFLICT` targets, don't add `NOT NULL`
  without a default.
- Deploy, swap, confirm prod is healthy on the new code.
- **Contract (a later deploy):** once no slot runs the old code, drop the old column/constraint.

If a recreate is genuinely required and the data is ephemeral (cooldown windows etc.), accept
that both old-code windows above will error for that table, and **swap promptly** to minimize
window #1 — but prefer expand/contract.

## Related incident — deploy race

The same merge cluster also hit a deploy race: #161 and #162 merged ~20s apart, their staging
deploys ran concurrently, and the icon-only build (#162, which predated #161) finished *last*
and overwrote #161 on the staging slot — so the first swap promoted a build missing #161
entirely. Fixed by giving `main_hurrahtv.yml` and `swap.yml` a shared `concurrency` group
(`hurrahtv-deploy-pipeline`, `cancel-in-progress: false`) so deploys and swaps serialize.

Related: [[blazor-css-cache-busting]], [[postgres-migration-tsv-truncation]].
