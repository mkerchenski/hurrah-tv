# beforeinstallprompt Fires Before WASM Boots — Capture Early, Bridge to Blazor

> **Area:** WASM | UI
> **Date:** 2026-05-26
> **Resolves:** mkerchenski/hurrah-tv#15

## Context
Adding the Android/desktop-Chrome install prompt to the Blazor WASM client. Chrome
fires a one-shot `beforeinstallprompt` event when it deems the app installable; you
must `preventDefault()` and stash it to trigger the install later. The `InstallBanner`
component decides whether to show an Install button.

## Learning

**The event fires too early for any Blazor-side listener to catch it.** Chrome fires
`beforeinstallprompt` during/shortly after page load — well before the WASM runtime
downloads and boots, and before a lazily-imported ES module (`js/install.js`) loads.
A listener registered inside the module, or inside a component's `OnAfterRenderAsync`,
will miss the event entirely.

**Capture it in an inline `<script>` in `index.html` `<head>`** — the earliest
execution point — and stash the deferred event on `window`. The lazy module reads it
later:

```js
// index.html, inline, before everything
window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    window.__hurrahInstallPrompt = e;
    if (window.__hurrahNotifyInstall) window.__hurrahNotifyInstall(); // late-event bridge
});
```

**Two timing cases, both must be handled:**

1. **Event already fired before the component mounts** (the common case — WASM boot is
   slow, so by the time `InstallBanner` reaches `OnAfterRenderAsync(firstRender)` the
   event is usually already stashed). A simple `canPromptInstall()` check on first
   render works here.
2. **Event fires *after* the component mounts** (Chrome's installability heuristics can
   delay it). A first-render-only check misses this until a full reload. Fix: the
   component subscribes a `DotNetObjectReference` callback; the module wires
   `window.__hurrahNotifyInstall` to invoke it (and fires it immediately if the event
   was already stashed). The same bridge handles Chrome *re-offering* after a declined
   prompt. Remember `@implements IDisposable` to dispose the `DotNetObjectReference`.

This is the same `DotNetObjectReference` + `[JSInvokable]` pattern the codebase already
uses for long-press (`LongPressService.AttachAsync`).

**`prompt()` is single-use** — calling it consumes the event, so null the stash after.
Chrome may emit a fresh `beforeinstallprompt` later if the user didn't install, which
re-triggers the bridge.

**Testing caveat:** Chrome won't fire a real `beforeinstallprompt` in an automated
DevTools session (engagement heuristics). Verify the wiring by checking the component
subscribed (`typeof window.__hurrahNotifyInstall === 'function'`), then *simulate* the
late event — set `window.__hurrahInstallPrompt` to a fake `{prompt(){}, userChoice}` and
call `window.__hurrahNotifyInstall()` — and assert the Install banner appears with no
reload.

## Example
```csharp
// InstallBanner.razor — subscribe once, react to late events
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;
    if (await InstallSvc.ShouldShowAsync()) { _variant = Variant.Ios; StateHasChanged(); return; } // iOS: no event
    _selfRef = DotNetObjectReference.Create(this);
    await InstallSvc.OnInstallAvailableAsync(_selfRef); // fires now if stashed, and on future events
}

[JSInvokable]
public async Task OnInstallAvailable()
{
    if (_variant != Variant.None) return;
    if (await InstallSvc.CanPromptInstallAsync()) { _variant = Variant.Install; await InvokeAsync(StateHasChanged); }
}
```
