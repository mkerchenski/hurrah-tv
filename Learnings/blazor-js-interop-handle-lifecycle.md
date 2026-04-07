# JS Interop Handle Lifecycle: InvokeVoidAsync Silently Discards Cleanup Handles

> **Area:** WASM
> **Date:** 2026-04-07

## Context
Registered per-element long-press touch handlers via JS interop. The JS function returns a cleanup handle object. Used `InvokeVoidAsync` initially, which discards the return value. Components couldn't call the cleanup function on dispose because they never received the handle. Every navigation leaked 4 event listeners per card element.

## Learning
When a JS function returns something you need for cleanup, you **must** use `InvokeAsync<IJSObjectReference>` — not `InvokeVoidAsync`. The `Void` variant silently discards the return value with no warning. This is easy to miss because the JS function still executes correctly; the leak only manifests over time as orphaned listeners accumulate.

**The full lifecycle pattern for per-element JS interop:**

1. **Register**: `InvokeAsync<IJSObjectReference>` — store the returned handle
2. **Track by ID**: Use `Dictionary<int, IJSObjectReference>` when items change dynamically (not a list or HashSet — you need to map handles back to items for selective cleanup)
3. **Clean up stale**: On re-render (`OnAfterRenderAsync`), diff current item IDs against dictionary keys. Call cleanup for IDs no longer present.
4. **Register new**: Register handlers for IDs not yet in the dictionary.
5. **Dispose all**: In `DisposeAsync`, iterate all remaining handles and call cleanup.

**Why `DotNetObjectReference` also needs careful lifecycle**: Each `DotNetObjectReference.Create(this)` pins the component in memory. If the JS side holds a reference after disposal, .NET callbacks will throw. Always dispose the ref in `DisposeAsync`.

## Example
```csharp
// BAD — handle silently lost, cleanup impossible
await JS.InvokeVoidAsync("hurrahLongPress", element, dotNetRef, "OnLongPress", itemId);

// GOOD — handle stored for cleanup
IJSObjectReference handle = await JS.InvokeAsync<IJSObjectReference>(
    "hurrahLongPress", element, dotNetRef, "OnLongPress", itemId);
_handles[itemId] = handle;

// cleanup stale items on re-render
List<int> stale = _handles.Keys.Where(id => !currentIds.Contains(id)).ToList();
foreach (int id in stale) {
    await JS.InvokeVoidAsync("hurrahLongPressCleanup", _handles[id]);
    _handles.Remove(id);
}
```
