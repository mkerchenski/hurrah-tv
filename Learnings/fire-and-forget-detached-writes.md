# Fire-and-Forget Detached Writes: Protection Surface vs. Theatre

> **Area:** API | AI
> **Date:** 2026-05-24
> **Resolves:** mkerchenski/hurrah-tv#121

## Context

PR #122 introduced `CurationService.TrackUsageDetachedAsync` to keep `AIUsage` rows landing even when the request scope tears down mid-write. Issue #121's acceptance criteria offered two shapes:

- **A.** `IHostedService` + bounded `Channel<AIUsageRecord>` consumed by a background loop with its own root-scope `IServiceScope`.
- **B.** Resolve a root-scope `IServiceScopeFactory` inside `CurationService`, create a fresh scope just for `TrackAIUsageAsync`, and run that write detached from the request scope.

The PR picked B. Three independent reviewers — Copilot's PR review bot, `/xsimplify`, and an `/xreview` finder agent — converged on the same handful of pitfalls in the implementation. The pattern works, but the surface area of "what it actually protects against" is much narrower than the comment originally claimed.

## Learning

"Detached fire-and-forget writes" is a stack of four separate concerns. Conflating them produces a method that *looks* robust but only guards one of them.

### 1. `IServiceScopeFactory` with a singleton dependency is theatre, not protection

`CurationService` resolves `DbService` from a fresh scope:

```csharp
using IServiceScope scope = _scopeFactory.CreateScope();
DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
await scopedDb.TrackAIUsageAsync(...);
```

If `DbService` is registered `AddSingleton` (it is, in `Program.cs:13`), `scope.ServiceProvider.GetRequiredService<DbService>()` returns the same instance as the captured `_db` field. The `using IServiceScope` allocates a scope that resolves zero scoped dependencies. The "fresh scope keeps the write independent of the request scope" claim is **only future-proofing for if `DbService` ever goes scoped** — it's not protecting against anything today.

If you want the comment to be honest, say so. The simplify pass's final wording:

> *"DbService is singleton today, so the fresh scope adds no protection; future-proofing for if DbService becomes scoped."*

### 2. `CreateScope()` must be inside the `try` block

`_scopeFactory.CreateScope()` and `GetRequiredService<DbService>()` can both throw during host shutdown — most commonly `ObjectDisposedException` when the root provider has begun disposing. If they're outside the inner `try/catch`, the Task faults silently:

```csharp
// WRONG — Task faults unobserved on shutdown
return Task.Run(async () =>
{
    using IServiceScope scope = _scopeFactory.CreateScope();   // can throw
    DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
    try
    {
        await scopedDb.TrackAIUsageAsync(...);
    }
    catch (Exception ex) { _logger.LogError(ex, "..."); }
});

// RIGHT — every failure path lands in the log
return Task.Run(async () =>
{
    try
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
        await scopedDb.TrackAIUsageAsync(...);
    }
    catch (Exception ex) { _logger.LogError(ex, "..."); }
});
```

Three reviewers flagged this independently — it's not subtle once you look for it, but easy to write wrong on the first pass.

### 3. Prefer `async Task` over `Task` returning `Task.Run`

`public Task Foo() => Task.Run(...)` is brittle: any synchronous throw between the method entry and the `return Task.Run(...)` call propagates to the caller, bypassing the `_ = Foo()` discard at fire-and-forget sites. There's no sync code there today, but the next contributor who adds `ArgumentException.ThrowIfNull(userId)` at the top will silently break callers — the exception fires the request thread instead of becoming a task fault.

`public async Task Foo() { await Task.Run(...); }` puts the entire body inside the async state machine, so sync throws also become task faults — observed by anyone who awaits, ignored by `_ =` callers. Cost: one extra state-machine allocation per call. For a fire-and-forget helper, that's the right trade.

### 4. `Task.Run` does NOT survive host shutdown

This is the load-bearing limitation of the entire pattern. ASP.NET's request-pipeline drain awaits in-flight requests; it does **not** await arbitrary thread-pool work. On `SIGTERM` / app-pool recycle / Azure deploy slot swap:

1. Anthropic inference completes ~50ms before shutdown.
2. Endpoint sends the response, `_ = TrackUsageDetachedAsync(...)` queues to the pool.
3. Host begins shutdown, root provider is disposed.
4. The queued continuation either runs against a disposed provider (throws `ObjectDisposedException` — at least logged by the inner try since point 2) or never runs (process exits).
5. Anthropic billed, `AIUsage` row missing.

The "we paid Anthropic so the row must land" promise in the original comment **cannot be kept** by `Task.Run` alone. The actual fix is option A from #121: an `IHostedService` with a `Channel<AIUsageRecord>` that drains on `StopAsync`, or registering the in-flight task set with `IHostApplicationLifetime` so the host awaits them on shutdown.

This is tracked as #123 — left out of #121's PR to keep scope tight.

## Recipe

The current `TrackUsageDetachedAsync` shape:

```csharp
public async Task TrackUsageDetachedAsync(
    string userId, int inputTokens, int outputTokens, decimal cost, string requestType)
{
    await Task.Run(async () =>
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
            await scopedDb.TrackAIUsageAsync(userId, inputTokens, outputTokens, cost, requestType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detached AIUsage write failed for {UserId} ({RequestType})", userId, requestType);
        }
    });
}
```

It protects against:
- Request scope teardown (today: trivially, because `DbService` is singleton).
- Sync throws in future param-validation code (because `async Task`).
- Shutdown-time `ObjectDisposedException` from scope creation (because the try block covers it).

It does **not** protect against host shutdown. Treat the "row must land" claim as best-effort, not a guarantee, until #123 lands an `IHostedService` outbox or task-set drain.

## Related

- #121 — original "AIUsage tracking can be lost when request scope tears down" issue.
- #123 — follow-up: "Detached AIUsage writes can be lost on host shutdown."
- #124 — adjacent: "Monthly AI budget cap can be exceeded under concurrent requests" — same fire-and-forget widens the existing budget-check race.
- `oce-rethrow-needs-token-filter.md` — different pattern, same family (when does cancellation cross a boundary vs. when doesn't it).
