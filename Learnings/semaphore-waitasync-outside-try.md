# `SemaphoreSlim.WaitAsync` Outside the Try Block Is Correct — Don't "Fix" It

> **Area:** API | .NET
> **Date:** 2026-05-24
> **Resolves:** mkerchenski/hurrah-tv#124

## Context

PR #130 added two static `SemaphoreSlim` gates to `CurationService` (`AiMatchGate`, `AiCurationGate`) bounding concurrent paid AI inferences. The acquire/release shape used:

```csharp
await AiCurationGate.WaitAsync(cancellationToken);
try
{
    // Anthropic call
}
catch (...) { ... }
finally
{
    AiCurationGate.Release();
}
```

Three independent reviewers — two `/xreview` finder agents and one `/xsimplify` candidate — flagged this as a "high-severity semaphore phantom-release bug." Each independently argued the same thing: "If `WaitAsync` throws OCE, the semaphore was never acquired, but `Release()` in the finally still fires, corrupting the count upward."

Each one was wrong. The code is correct. But the pattern *looks* like the wrong-pattern at a glance, and the corrupt-count argument is plausible enough that consensus across reviewers doesn't disprove it.

## Learning

In C#, the `finally` of a `try` statement only runs when control has entered the `try` block. If the statement that precedes the `try` throws — including an `await` expression that comes before it — the `finally` does NOT execute.

```csharp
async Task M()
{
    await Foo();        // throws
    try { ... }
    finally { Bar(); }  // NEVER reached — Foo's throw propagates past the try
}
```

So when `await AiCurationGate.WaitAsync(ct)` throws `OperationCanceledException` (either because `ct` was already cancelled or fired during the wait), control never enters the `try` block, the `finally` never runs, and `Release()` is never called. The semaphore count is correct: it stays at whatever value it had before the failed `Wait`.

This means the canonical idiom many reviewers expect:

```csharp
bool acquired = false;
try
{
    await gate.WaitAsync(ct);
    acquired = true;
    // ...
}
finally
{
    if (acquired) gate.Release();
}
```

…is identical in behavior to:

```csharp
await gate.WaitAsync(ct);
try
{
    // ...
}
finally
{
    gate.Release();
}
```

Both correctly release exactly once on the success path and never release on the throw-during-wait path. The second form is shorter and arguably cleaner. The first form is more familiar to reviewers — including LLM reviewers, which have probably seen the `bool acquired` flag idiom thousands of times in training data — so it survives review without flags.

## Trade-off

| Form | Pros | Cons |
|------|------|------|
| WaitAsync outside `try` | Fewer lines; no extra flag; release intent is unambiguous | Reviewers (human and AI) keep flagging it as a phantom-release bug |
| `bool acquired` flag inside `try` | Reviewers recognize the canonical pattern | Extra flag; longer; subtly suggests the flag is load-bearing when it isn't |

Both work. Pick one and stick with it across the codebase. If you pick "WaitAsync outside the try" (as `CurationService` does), expect to defend it on every review and keep this learning linked.

## Example

`HurrahTv.Api/Services/CurationService.cs:211` and `:407` — both gates use the WaitAsync-outside-try shape. Both have been confirmed correct against three converging false-positive flags.

## Related

- [[hosted-service-run-owns-dispatch]] — the same PR; another non-obvious correctness pattern that survives reviewer scrutiny.
- [[fire-and-forget-detached-writes]] — same surface area; this learning's mirror image (correctness pattern that *looks* fine but isn't).
