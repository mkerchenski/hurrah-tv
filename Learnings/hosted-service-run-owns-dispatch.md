# IHostedService Should Own the `Task.Run` Dispatch, Not Just `Register(Task)`

> **Area:** API | .NET
> **Date:** 2026-05-24
> **Resolves:** mkerchenski/hurrah-tv#123

## Context

`AIUsageDrainHostedService` exists to make fire-and-forget AIUsage writes survive host shutdown — Anthropic was already paid, so the cost row must land. The first cut exposed a simple `Register(Task)` API:

```csharp
// caller:
public Task TrackUsageDetachedAsync(...)
{
    Task task = Task.Run(async () => { ... });
    _drain.Register(task);
    return task;
}

// drain service:
public void Register(Task task)
{
    if (task.IsCompleted) return;
    _inFlight.TryAdd(task, 0);
    _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
}
```

`/xsimplify` and `/xreview` both flagged a race window between `Task.Run` returning and `Register` adding to `_inFlight`. The window is two synchronous instructions on the same thread — microseconds wide — but real: a concurrent `StopAsync` running on the shutdown thread can snapshot `_inFlight.Keys` between the two and miss the task entirely. The task then runs to disposal-or-process-exit unguarded. Exactly the SIGTERM/deploy-swap loss window the service was built to close.

## Learning

When a hosted service tracks fire-and-forget Tasks, **the service must own the dispatch**, not just observe Tasks the caller created. Invert the API:

```csharp
// drain service:
public Task Run(Func<CancellationToken, Task> work)
{
    TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // add BEFORE Task.Run schedules — once Run returns, the tcs.Task is observable
    // to any concurrent StopAsync. There is no window where the task exists but
    // _inFlight doesn't know about it.
    _inFlight.TryAdd(tcs.Task, 0);
    _ = tcs.Task.ContinueWith(static (t, state) =>
        ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
        _inFlight, TaskScheduler.Default);

    _ = Task.Run(async () =>
    {
        using CancellationTokenSource cts = new(InnerWriteTimeout);
        try { await work(cts.Token); }
        catch { /* defense-in-depth; caller's delegate handles its own exceptions */ }
        finally { tcs.TrySetResult(); }
    });

    return tcs.Task;
}

// caller:
public Task TrackUsageDetachedAsync(...)
{
    return _drain.Run(async ct =>
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
            await scopedDb.TrackAIUsageAsync(..., ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* log Warning */ }
        catch (Exception ex) { /* log Error */ }
    });
}
```

Key properties of `Run`:

1. **Registration is atomic with task creation.** The `TaskCompletionSource` is added to `_inFlight` BEFORE `Task.Run` schedules the thread-pool work. A concurrent `StopAsync` cannot snapshot an empty dict during the gap, because there is no gap.
2. **The inner `CancellationTokenSource` bounds the work** so a slow write can't outlive the drain budget. Tighter than the drain timeout (8s inner vs 10s drain) so cancellation propagates before the host gives up.
3. **The TCS always completes successfully** (`TrySetResult` in `finally`) — the inner work's exceptions are the caller's responsibility, not the drain's. This is a deliberate fault contract: the drain awaits `Task.WhenAll(snapshot).WaitAsync(linkedCts.Token)` for *cancellation*, not fault observation. If the WhenAll could fault, it would propagate out of `StopAsync` and abort host shutdown.

## StopAsync companion pattern

```csharp
public async Task StopAsync(CancellationToken cancellationToken)
{
    Task[] snapshot = [.. _inFlight.Keys];
    if (snapshot.Length == 0) return;

    // host-CT already cancelled — drain budget is zero. Log honestly.
    if (cancellationToken.IsCancellationRequested)
    {
        LogDrainAborted(snapshot.Count(t => !t.IsCompleted));
        return;
    }

    LogDraining(snapshot.Length);

    using CancellationTokenSource timeoutCts = new(DrainTimeout);
    using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, timeoutCts.Token);

    try
    {
        await Task.WhenAll(snapshot).WaitAsync(linkedCts.Token);
        LogDrainComplete();
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        LogDrainTimeout(DrainTimeout.TotalSeconds, snapshot.Count(t => !t.IsCompleted));
    }
    catch (OperationCanceledException)
    {
        LogDrainAborted(snapshot.Count(t => !t.IsCompleted));
    }
}
```

Three properties matter:

1. **Pre-cancelled host CT gets an early-out**, not the `Task.Delay` race. Otherwise `Task.Delay(10s, cancelledCT)` returns a synchronously-cancelled Task; `Task.WhenAny` picks it; you log "timed out after 10s" though zero time elapsed.
2. **`Task.WhenAll(...).WaitAsync(token)` instead of `Task.WhenAny(work, Task.Delay)`** — disposes the timeout timer when work wins (`using CancellationTokenSource`), no orphan timer leak. The same trick from `api-await-with-timeout.md`, applied one layer up.
3. **`snapshot.Count(t => !t.IsCompleted)` not `_inFlight.Count`** — the dictionary state at log-time includes new Registrations and ContinueWith removals; snapshot's not-yet-completed count is what "may be lost" really means.

## DI registration

Register the service as both a singleton (so `CurationService` can inject it) and as a hosted service (so the host calls `StartAsync` / `StopAsync`). The factory ensures both resolve to the same instance:

```csharp
builder.Services.AddSingleton<AIUsageDrainHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIUsageDrainHostedService>());
```

## Related

- [[fire-and-forget-detached-writes]] — predicted this exact follow-up: "the actual fix is option A from #121: an IHostedService with a Channel<AIUsageRecord> that drains on StopAsync, or registering the in-flight task set with IHostApplicationLifetime so the host awaits them on shutdown."
- [[api-await-with-timeout]] — the `CancellationTokenSource + WaitAsync` pattern is the same trick applied to a different problem (bounded refresh wait vs. bounded drain wait).
- [[semaphore-waitasync-outside-try]] — same PR; same general theme of "non-obvious correctness pattern that survives review."
