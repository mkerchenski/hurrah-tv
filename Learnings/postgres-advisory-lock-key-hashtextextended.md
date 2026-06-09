# Use `hashtextextended` (64-bit), Not `hashtext` (32-bit), for Per-Key Advisory Locks

> **Area:** Data | API
> **Date:** 2026-06-09
> **Resolves:** mkerchenski/hurrah-tv#163

## Context
The fix for #163 (concurrent same-user queue adds colliding on `Position`) wraps the `MAX(Position)+INSERT` in a transaction guarded by a per-user advisory lock:

```sql
SELECT pg_advisory_xact_lock(hashtext(@UserId))
```

The intent (and the code comment) was "keyed per-user, so unrelated users never contend." During /xreview, multiple reviewers flagged that this isn't quite true.

## Learning
`pg_advisory_xact_lock(bigint)` takes a 64-bit key, but **`hashtext()` returns `int4` (32-bit)**. The int4 is implicitly widened to bigint, so the effective key space is only 2³² (~4.3 billion) values, not 2⁶⁴. Two distinct keys (here, user IDs) whose 32-bit hashes collide map to the **same lock slot** and serialize against each other — so a "per-user" lock can occasionally block unrelated users. No data corruption (the lock still prevents the position race for both), just surprise contention, and a comment that's subtly false.

Use **`hashtextextended(text, seed)`**, which returns `bigint` — the full 64-bit key space. Available since Postgres 11 (Azure PG Flexible Server qualifies). Verify quickly: `SELECT pg_typeof(hashtextextended('abc', 0));` → `bigint`.

The 32-bit form is functionally fine at tiny scale (collision needs two specific users adding concurrently), but `hashtextextended` is a one-word change that dramatically reduces collision risk (any fixed-size hash can still theoretically collide) and makes the comment honest.

## Example
```sql
-- 32-bit: int4 silently widened → only 2^32 distinct lock slots, cross-key collisions possible
SELECT pg_advisory_xact_lock(hashtext(@UserId));

-- 64-bit: full key space, distinct keys never share a slot
SELECT pg_advisory_xact_lock(hashtextextended(@UserId, 0));
```
Related: [[serialize-writes-when-server-ignores-aborts]] (the other "serialize concurrent writes" angle — client serialization vs. DB-side locking).
