# Pre-Blanking State Before Cancellable Work Defeats Cancel-Safety

> **Area:** API
> **Date:** 2026-05-24

## Context

`/api/curation/refresh` (the manual "regenerate my AI rows" endpoint) had this shape before PR #130:

```csharp
cache.Set(rateLimitKey, true, RefreshCooldown);              // 5-min anti-abuse lockout
await db.SetCurationCacheAsync(userId, "[]", "force-refresh"); // sentinel: invalidate the cache row

// ... await the watchlist + provider reads + AI inference ...
```

The `"force-refresh"` sentinel was load-bearing: `GetCuratedRowsAsync` checks the cache and only regenerates from AI if the cached `WatchlistHash` doesn't match the current one. Writing `"force-refresh"` as the hash guarantees a miss, forcing the rerun.

PR #130 threaded `CancellationToken` through the curation endpoints — caller-driven OCE now translates to 499. Within a week, `/xsimplify` flagged a real consequence: if the user navigates away within ~50ms of tapping Refresh, the rate-limit and the cache-blank are both already committed, but the AI work was cancelled. Net state:

- Cache row contains `"[]"` with hash `"force-refresh"` (no real rows).
- Rate-limit cache key is set for 5 minutes.

The next time the user opens Home, `/api/curation/rows` reads the cache: it sees `"[]"` and returns no rows. The user sees the no-curated-rows fallback even though their watchlist is unchanged. And `/refresh` is locked out for the rest of the cooldown window. The user can't unstick themselves without waiting.

## Learning

Any pre-cancellation state mutation that drives later behavior is a leak waiting to happen the moment a cancellation path becomes common. The pattern to avoid:

```csharp
// BAD — sentinel state writes BEFORE the cancellable work
MutateExternalState();                  // synchronous, can't be cancelled
await DoCancellableWork(ct);            // OCE here strands the sentinel
```

Two safer rewrites:

**Option A — write the sentinel at the END of successful work:**

```csharp
await DoCancellableWork(ct);
MutateExternalState();                  // only runs on success
```

Won't work if the sentinel needs to drive intermediate behavior (it did here — the cache check happens INSIDE the cancellable chain, before the AI call).

**Option B — pass a flag through to skip the cached path, without writing the sentinel at all:**

```csharp
await DoCancellableWork(ct, forceRegenerate: true);
```

Inside the work:

```csharp
var cached = forceRegenerate ? null : await db.GetCurationCacheAsync(userId, ct);
if (cached != null && cached.Hash == currentHash) return cached;
// regen path
await db.SetCurationCacheAsync(userId, freshRows, currentHash, ct);  // overwrites on success
```

Now a mid-flight cancel touches no external state. The cache row stays at whatever the previous successful regen produced. The rate-limit may still be set (it's the anti-abuse cost, and is intentional). PR #130 picked Option B.

The general principle: **state that drives later behavior should only land on the success path of the work it's tagging.** Anti-abuse counters can land early (the user attempted the action), but anything that says "the previous data is invalid" must wait for the new data to actually exist.

## Why this only became a bug after CT threading

Pre-#130, the request always ran to completion server-side regardless of client navigation — the cache row got overwritten with real AI data even if the user never saw it. The pre-blank was free.

Threading CT made cancellation a real outcome. Any code that relied on "the request always completes" as an unstated invariant became a bug the moment that invariant broke.

This is a recurring pattern: cancellation is a viral correctness property. Every state mutation in the request pipeline needs to be re-examined when CT is added, because mutations that were idempotent-over-completion become permanent-on-partial-execution.

## Example

`HurrahTv.Api/Endpoints/CurationEndpoints.cs` `/refresh` handler post-#130, and `HurrahTv.Api/Services/CurationService.cs:89` for the `forceRefresh` flag on `GetCuratedRowsAsync`.

## Related

- [[hosted-service-run-owns-dispatch]] — same PR; the inner-write CT timeout fix is the mirror image (mutation that should complete even when the caller's CT cancels).
- [[oce-rethrow-needs-token-filter]] — different surface, same family: cancellation as a viral correctness property.
- [[api-await-with-timeout]] — the bounded-refresh pattern that handles a similar trade-off (wait-with-timeout vs return stale).
