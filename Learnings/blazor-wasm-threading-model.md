# Blazor WASM Is Single-Threaded — Server Threading Concerns Don't Apply

> **Area:** WASM
> **Date:** 2026-04-07

## Context
Every code review in this session flagged `async void` event handlers, `System.Timers.Timer` callbacks, and `StateHasChanged()` from non-lifecycle contexts as major/critical issues. These are real concerns in Blazor Server (multi-threaded, synchronization context matters) but are non-issues in Blazor WASM.

## Learning
Blazor WebAssembly runs entirely in the browser on a **single thread**. There is no multi-threading, no thread pool (it's emulated on the same thread), no synchronization context to marshal to. This means:

1. **`async void` on event handlers** — safe. The "crashes the process" concern doesn't apply because there is no process. Unhandled exceptions in WASM show in the browser console and trigger `blazor-error-ui`, same as any other unhandled exception. Standard event handler pattern `async void OnSomething(...)` is correct.

2. **`System.Timers.Timer` Elapsed callback** — runs on the same thread as UI. No marshaling needed. `InvokeAsync` is a no-op passthrough in WASM (it's only meaningful in Blazor Server for cross-thread dispatch). Using it is harmless but not required.

3. **`StateHasChanged()` from any context** — always safe. Whether called from a lifecycle method, event handler, timer callback, or JS interop callback, it's always on the UI thread because there IS only one thread.

4. **Race conditions between `_disposed` check and `StateHasChanged`** — impossible. Single-threaded execution means the check and the call are atomic. No interleaving.

5. **`DotNetObjectReference` callbacks from JS** — execute on the main thread. No threading concern.

**When to still be careful:**
- `ObjectDisposedException` after navigation — a component can be disposed between an `await` and the continuation. The `_disposed` guard is about lifecycle, not threading.
- Fire-and-forget `_ = SomeAsync()` — the task isn't tracked by Blazor's renderer, so `StateHasChanged` inside may not trigger re-renders (see `blazor-async-statehaschanged.md`). This is a Blazor lifecycle issue, not a threading issue.

## Example
```csharp
// ALL of these are safe in Blazor WASM:

// async void event handler — standard pattern
private async void OnNavigated(object? sender, EventArgs e)
{
    await SomeAsyncWork();
    StateHasChanged(); // safe — same thread
}

// timer callback — same thread, no marshaling needed
_timer.Elapsed += (_, _) => _ = InvokeAsync(DoWork);
// InvokeAsync is a no-op here but doesn't hurt

// JS interop callback — same thread
[JSInvokable]
public void OnLongPress(int id) => DoSomething(id); // safe
```
