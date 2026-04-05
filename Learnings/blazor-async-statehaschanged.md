# Blazor Fire-and-Forget StateHasChanged Doesn't Work

> **Area:** WASM | UI
> **Date:** 2026-03-29

## Context
The AI curated section on the home page was invisible — no loading state, no error, nothing. The `LoadAICuration()` method was called via `_ = LoadAICuration()` (fire-and-forget) and called `StateHasChanged()` internally to update the UI.

## Learning
In Blazor WASM, `_ = SomeAsyncMethod()` that calls `StateHasChanged()` inside does NOT reliably trigger re-renders. The component lifecycle doesn't track fire-and-forget tasks, so `StateHasChanged()` calls from that context are silently ignored.

The fix is one of:
1. **`await` the method** from within a lifecycle method (`OnInitializedAsync`, `OnParametersSetAsync`) — Blazor renders at each yield point
2. **Use `InvokeAsync(StateHasChanged)`** if calling from a non-lifecycle context (e.g., timer callback)
3. **Guard with `IDisposable`** if the component might be disposed before the task completes

## Example
```csharp
// BAD — StateHasChanged silently fails
_ = LoadAICuration();

// GOOD — Blazor tracks the task and renders at yield points
await LoadAICuration();

// GOOD — for disposal safety
private bool _disposed;
public void Dispose() => _disposed = true;

private async Task LoadData()
{
    _loading = true;
    if (!_disposed) StateHasChanged();
    var data = await Api.GetDataAsync();
    _loading = false;
    if (!_disposed) StateHasChanged();
}
```

Also affects `Task.Delay` patterns (e.g., showing a checkmark for 1.5s then reverting) — if the user navigates away during the delay, `StateHasChanged()` fires on a disposed component.

## Additional: Lifecycle StateHasChanged Timing
In `OnParametersSetAsync`, setting `_loading = true` before the first `await` does NOT render the spinner — Blazor renders after the entire method completes, not at each statement. You must call `StateHasChanged()` explicitly before the first `await` to trigger an intermediate render.

## Additional: System.Timers.Timer in WASM
`System.Timers.Timer` fires on a threadpool thread and captures `this`. If the component is destroyed while the timer is pending, the `Elapsed` callback fires on a dead component. Always `@implements IDisposable` and stop/dispose the timer in `Dispose()`. Prefer `CancellationTokenSource` + `Task.Delay` (as in Search.razor) over `System.Timers.Timer` when possible.
