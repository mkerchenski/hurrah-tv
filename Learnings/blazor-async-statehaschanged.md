# Blazor Fire-and-Forget StateHasChanged May Not Re-render

> **Area:** WASM | UI
> **Date:** 2026-03-29 (revised 2026-06-09)

## Context
The AI curated section on the home page was invisible — no loading state, no error, nothing. The `LoadAICuration()` method was called via `_ = LoadAICuration()` (fire-and-forget) and called `StateHasChanged()` internally to update the UI.

## Learning
Two things are easy to conflate here — keep them separate:

- **Is `StateHasChanged()` safe to call from this context?** Always yes. Blazor WASM is single-threaded, so there's no thread/sync-context concern from any caller (lifecycle, event handler, timer, JS interop, or a fire-and-forget continuation). See `blazor-wasm-threading-model.md` — that's the canonical source for the threading half.
- **Will it actually re-render?** Not reliably from an *untracked* `_ = SomeAsyncMethod()`. Blazor's renderer doesn't track fire-and-forget tasks, so a `StateHasChanged()` in that continuation isn't guaranteed to flush a render — and the original "invisible AI section" was as likely a swallowed exception in the untracked task (see `blazor-wasm-async-event-exceptions.md`) as a missed render. This is a **lifecycle** issue, not a threading one.

So the earlier "silently ignored" framing was too strong: a guarded `StateHasChanged()` in the `finally` of an **`await`ed** method *does* render (Blazor renders at each yield point of a tracked task). The failure mode is specifically the `_ =` discard, not the call itself.

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

## Related
- `blazor-wasm-threading-model.md` — canonical source for "is `StateHasChanged()` / `async void` / a timer callback **safe** to call?" (always yes in WASM). This file is the canonical source for "will a fire-and-forget `StateHasChanged()` **re-render**?" (not reliably). Threading vs lifecycle — two different questions, one source of truth each.
- `blazor-wasm-async-event-exceptions.md` — why the original symptom may have been a swallowed exception, not a missed render.
