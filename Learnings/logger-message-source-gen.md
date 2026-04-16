# `[LoggerMessage]` Source Generation Fixes CA1873 Without IsEnabled Guards

> **Area:** API | .NET
> **Date:** 2026-04-16

## Context
`dotnet format --severity info` surfaced a single persistent finding — `CA1873: Evaluation of this argument may be expensive and unnecessary if logging is disabled` — on a `_logger.LogInformation(...)` call that passed `int` and `decimal` values as trailing params. The values were all cheap local variables; the "expense" the analyzer flagged wasn't the *values*, but the hidden `object?[]` allocation and boxing that `LoggerExtensions.LogXyz(ILogger, string, params object?[])` forces on every call, regardless of whether the log level is enabled.

## Learning
**The canonical fix for CA1873 is source-generated logging via `[LoggerMessage]`, not `IsEnabled` guards.**

The source generator (shipped in `Microsoft.Extensions.Logging.Abstractions` since .NET 6) emits a concrete non-generic logging method at compile time that:

1. Accepts parameters by their real types (`string`, `int`, `decimal`) — no `params object?[]`.
2. Wraps them in a lazy `LogValues` struct that only allocates when the log level is enabled.
3. Pre-computes the `EventId` and message template once as a static field, not on each call.

Three requirements:
- **Class must be `partial`** — the generator emits the method body into a sibling `.g.cs` file.
- **Method must be `partial void`** with the `[LoggerMessage]` attribute providing `Level` and `Message`.
- **Placeholder names in `Message` must match the parameter names.** The generator validates this at compile time — a typo produces `SYSLIB1013`, making the refactor safer than string templates.

The `IsEnabled` guard alternative works but clutters the code with one extra line per log call and still allocates on the hot path. `[LoggerMessage]` is both faster and cleaner.

## Example
```csharp
public partial class CurationService
{
    // ... fields, ctor, methods ...

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI curation for {UserId}: {InputTokens} in / {OutputTokens} out = ${Cost:F4}")]
    private partial void LogCurationUsage(string userId, int inputTokens, int outputTokens, decimal cost);
}

// call site:
LogCurationUsage(userId, inputTokens, outputTokens, cost);
```

## Verification
`dotnet format --verify-no-changes --severity info` exits clean after the conversion. No more boxed-arg allocation, and the log template is validated at compile time.

## When it's overkill
For a single throwaway debug log in a prototype, the one-time `if (_logger.IsEnabled(LogLevel.Debug))` guard is fine. For any production log that runs in a hot path — especially ones that pass value types — `[LoggerMessage]` is the right move.
