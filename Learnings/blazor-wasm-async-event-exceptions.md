# Async Event Handlers in Blazor WASM Silently Swallow Exceptions

> **Area:** WASM
> **Date:** 2026-04-08

## Context
`SelectStatus` in `Queue.razor` was clearing `_expandedStatusId` before the API `await`, then mutating `item.Status` after it. If the API call threw, the picker was already closed and no state update happened — but the user saw the picker disappear and assumed success. No error was surfaced.

## Learning
In Blazor WASM, unhandled exceptions from async event handlers (`async Task` or `async void` methods bound to `@onclick`) are caught by the framework and logged to the browser console. They do **not** propagate to the UI, do not trigger the `blazor-error-ui` banner, and do not notify the user. The component continues rendering normally as if nothing happened.

This means any state mutation that happens *after* a throwing `await` is silently skipped:

```csharp
// BAD — if UpdateStatusAsync throws:
// - _expandedStatusId is already null (picker closed)
// - item.Status is never updated
// - user thinks it worked
private async Task SelectStatus(QueueItem item, QueueStatus status)
{
    _expandedStatusId = null;
    await Api.UpdateStatusAsync(item.Id, status); // throws
    item.Status = status;    // never runs
    UpdateCounts();          // never runs
}

// GOOD — state only changes on success
private async Task SelectStatus(QueueItem item, QueueStatus status)
{
    _expandedStatusId = null;
    try
    {
        await Api.UpdateStatusAsync(item.Id, status);
        item.Status = status;
        UpdateCounts();
    }
    catch { }
}
```

**Rule:** Any async event handler that mutates UI state based on an API response needs try/catch. The "catch" block can be empty if silent failure is acceptable, but the mutations must be inside the `try`.

## Related
- `blazor-wasm-threading-model.md` — `async void` vs `async Task` safety
- `blazor-async-statehaschanged.md` — render timing after async operations
