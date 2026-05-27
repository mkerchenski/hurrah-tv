# Test Single-Threaded WASM Async Logic by Driving It Inline

> **Area:** WASM | UI
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#102

## Context

`FieldSaver` (the per-control auto-save coordinator on `/settings`, PR #143) became a
non-trivial async state machine: debounce → single-slot queue → serial drain, with
last-write-wins ordering. That ordering guarantee is exactly the kind of subtle logic worth a
regression test — but it depends on Blazor WASM being single-threaded (see
[[blazor-wasm-threading-model]]). Running the test on desktop .NET, where `Task` continuations
hop to the thread pool, would *reintroduce* the very races that can't happen in production: a
stale read of `_draining` could start a second drain worker and the test would flake (or worse,
pass while exercising behavior that never occurs in the browser).

The naive fixes are both bad: real `Task.Delay` waits make the test slow and timing-flaky, and
installing a custom single-threaded `SynchronizationContext` is exactly the shared test-base
machinery this repo's testing conventions tell us to avoid.

## Learning

You can drive a single-threaded-WASM async component **inline on the test thread** — no waiting,
no sync-context — by removing every source of a thread hop:

1. **`debounceMs: 0`** — `Task.Delay(0)` returns an already-completed task, so `await`-ing it
   continues synchronously on the same thread instead of posting to a timer/pool.
2. **Gate each unit of work on a `TaskCompletionSource`** — a plain TCS (no
   `RunContinuationsAsynchronously`) runs its continuations *synchronously on the thread that
   calls `SetResult`*. So when the test thread completes a gate, the component's continuation
   (and everything it triggers) runs inline on the test thread too.

The result: calling `Schedule(...)` runs the save lambda synchronously up to its first *real*
pending await (the gate); `gate.SetResult()` resumes it — and the next queued save — inline. The
whole state machine executes deterministically on one thread, faithfully matching WASM. Assertions
read shared state with no locks because nothing else is running.

Keep the *terminal* save gated-but-uncompleted so the drain parks at `await save()` and never
reaches a real delay (e.g. the post-save "Saved" display `Task.Delay(1500)`), which *would* hop.
Then `Dispose()` and let the parked task be abandoned.

## Example

```csharp
private static FieldSaver NewSaver() => new(() => Task.CompletedTask, debounceMs: 0);

[Fact]
public void Saves_DoNotOverlap_SecondWaitsForFirstToComplete()
{
    List<int> started = [];
    TaskCompletionSource gate1 = new();
    TaskCompletionSource gate2 = new();
    FieldSaver saver = NewSaver();

    saver.Schedule(async () => { started.Add(1); await gate1.Task; });
    Assert.Equal(new[] { 1 }, started);      // ran inline up to the gate

    saver.Schedule(async () => { started.Add(2); await gate2.Task; });
    Assert.Equal(new[] { 1 }, started);      // queued, not started — serialized

    gate1.SetResult();                        // resumes #1, then the drain runs #2 — all inline
    Assert.Equal(new[] { 1, 2 }, started);

    saver.Dispose();                          // #2 parked on gate2, abandoned; no background work
}
```

38 tests, 28 ms, zero flake. The trick only works for logic that *assumes* single-threading — which
is the point: the test is faithful precisely because it can't tolerate a hop, same as the runtime.

## Related
- [[blazor-wasm-threading-model]] — why the single-thread assumption is valid in production
- [[serialize-writes-when-server-ignores-aborts]] — the ordering logic these tests pin
