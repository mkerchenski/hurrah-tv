# Last-Write-Wins Needs Client Serialization, Not Cancellation, When the Server Ignores Aborts

> **Area:** WASM | API | Data
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#102

## Context

The first cut of `/settings` per-control auto-save (PR #143) used a "cancel-and-race" design: a new
change cancelled the previous request's `CancellationToken` and fired its own. A version counter
guarded the UI/cache against a stale in-flight result (the [[optimistic-update-version-token]]
pattern). The Copilot PR review and the API-safety reviewer agent both flagged that this still
leaves the **server** wrong on rapid changes (off→on→off): the version token only governs what the
*client* displays.

The settings endpoints (`MapPut` → `SaveUserSettingsAsync`) take no `CancellationToken` and don't
observe `RequestAborted`. So when the client cancels an in-flight request, the HTTP send aborts but
the server keeps processing and commits anyway.

## Learning

**Cancelling an in-flight HTTP request does not stop the server from committing it.** If two writes
to the same row overlap and the server can't be made to honor the abort, an earlier (slower) write
can commit *after* a newer one — the final persisted state is wrong even though every client-side
guard (optimistic revert, version token, cache) looks correct.

A version/sequence token solves the *display* race, not the *persistence* race. For ordered writes
from a single client, the fix is to **serialize**: keep at most one request in flight per logical
target, and run the next change only after the current one fully completes (response received). Then
requests reach the server in submission order, so it commits them in order. Pair it with a
single-slot "newest pending" queue so a burst coalesces to the latest value (last-write-wins)
instead of replaying every intermediate.

This makes per-request cancellation unnecessary — saves run to completion. That's the right call
here: aborting wouldn't have stopped the commit anyway, and completing a save the user triggered
just before navigating away is the desired behavior, not a bug.

Scope check before reaching for the heavier hammer: server-side concurrency control (rowversion /
ETag, or threading `CancellationToken` into the DB write) is only worth it for genuinely concurrent
*multi-client* writes to the same row. User settings are edited by one client at a time, so
client-side serialization is sufficient and keeps the fix out of the API and schema entirely.

## Example

```csharp
// the queue: newest pending replaces an un-started one; one worker drains it serially
private void QueueAndDrain(Func<Task> save)
{
    _pending = save;        // last write wins
    _ = Drain();
}

private async Task Drain()
{
    if (_draining) return;  // a worker is already running; it will pick up _pending
    _draining = true;
    try
    {
        while (_pending is { } save)
        {
            _pending = null;
            try { await save(); } catch { /* surface Failed */ }
            // a newer change that arrived mid-save is now in _pending → loop runs it next,
            // strictly after this one's response. requests never overlap.
        }
    }
    finally { _draining = false; }
}
```

## Related
- [[optimistic-update-version-token]] — guards the UI/cache race; this learning covers the
  server-persistence race it does *not* cover
- [[fire-and-forget-detached-writes]] — related fire-and-forget save shape
- [[testing-wasm-async-inline-single-thread]] — how the serialization guarantee is tested
