# Fire-and-Forget API Refreshes Give Stale Data on First Load

> **Area:** API | WASM
> **Date:** 2026-04-07

## Context
The queue endpoint refreshed stale episode dates via `_ = Task.WhenAll(...)` — fire-and-forget. The response returned immediately with cached data. Users had to reload the page a second time to see updated "1 day" / "Tomorrow" badges. Jimmy Kimmel showed no upcoming badge in the morning even though the episode was already in the database.

## Learning
Fire-and-forget in a GET endpoint means the first request after staleness always returns old data. The refresh happens, but the response is already gone. For data that changes daily (episode air dates), this creates a jarring "it's wrong until I refresh" experience.

The fix is `WaitAsync` with a `CancellationTokenSource` timeout — wait up to N seconds for the refresh, then fall back to stale data if it's slow. This gives the best of both worlds: fresh data when TMDb responds quickly (usually <1s), bounded latency when it doesn't.

Key gotchas discovered during review:
- **`Task.WhenAny(work, Task.Delay(n))` leaks the timer** — when work finishes first, the delay timer keeps ticking. Use `CancellationTokenSource` + `WaitAsync(token)` instead, and wrap in `using` so the timer is disposed.
- **Inner tasks aren't cancelled by the CTS** — they continue running after the timeout. This is actually fine here: the DB writes are useful work that benefits the next request. But be aware the timeout only bounds the *wait*, not the *work*.
- **`OperationCanceledException` from inner tasks** can be misattributed as a timeout if the outer catch is too broad. Use `catch (Exception ex) when (ex is not OperationCanceledException)` in inner tasks to let cancellation propagate correctly.

## Example
```csharp
// BAD — user always sees stale data on first load
_ = Task.WhenAll(staleItems.Select(item => RefreshAsync(item)));
return Results.Ok(items);

// GOOD — bounded wait with proper cleanup
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
try
{
    await Task.WhenAll(staleItems.Select(item => RefreshAsync(item)))
        .WaitAsync(cts.Token);
    items = await db.GetQueueAsync(userId); // re-read with fresh data
}
catch (OperationCanceledException)
{
    // timeout — return stale data, refresh completes in background
}
```
