# Suppressing iOS Safari's Native Image Callout on Custom Long-Press Gestures

> **Area:** UI | WASM
> **Date:** 2026-04-16

## Context
Poster cards in Hurrah.tv have a custom 500ms long-press that opens the QuickActions menu. On iOS Safari, ~75% of the time a native "peek" sheet hijacked the gesture first — big-poster preview plus a Share/Save-image menu. Dismissing it was required before the user could see the app's menu. The existing `hurrahLongPress` JS (documented in `mobile-long-press-gestures.md`) correctly handled app-level gesture conflicts, but those fixes did nothing against the native callout, because the native callout isn't driven by the `click` event — iOS decides during the live touch sequence whether to render the preview.

## Learning
**Suppressing iOS's native image callout requires three changes together — missing any one leaves the peek firing intermittently:**

1. **CSS on the `<img>`:** `-webkit-touch-callout: none` tells Safari "don't offer the save/share sheet for this image." Add `user-select: none` and `draggable="false"` as defense in depth (the latter prevents the drag-preview animation on desktop Safari too). The property is **not inherited** — put it on the `<img>` itself, not just the wrapper. Putting it on the wrapper does nothing because Safari inspects the *touch target* element.

2. **Non-passive `touchstart` registration:** by default `addEventListener('touchstart', fn, { passive: true })` is used for scroll perf. Passive listeners **cannot** call `preventDefault` — the browser ignores the call. To suppress the callout you must register as `{ passive: false }`. The scroll-perf cost is localized to poster cards; cancel-on-move logic is unchanged.

3. **Call `preventDefault()` on the live touch event inside the fired-timer callback:** store the `touchstart` event in a closure on start, and once the long-press timer fires, call `preventDefault()` on that stored event before invoking the .NET callback. This must run *before* `click` — by the time `click` fires, Safari has already decided to show the callout.

Add a `contextmenu` listener as belt-and-suspenders: some iOS versions route the peek through a synthetic contextmenu, so `e.preventDefault()` there closes the fallback path.

## Example
```javascript
// live touch event stash + cleanup on end/cancel
let activeEvent = null;
const onStart = (e) => {
    activeEvent = e;
    timer = setTimeout(() => {
        fired = true;
        try { activeEvent?.preventDefault(); } catch {}     // kills native callout
        dotNetRef.invokeMethodAsync(callbackName, itemId);
    }, 500);
};
const onEnd = () => { clearTimeout(timer); activeEvent = null; };
const onContextMenu = (e) => { if (fired) e.preventDefault(); };

el.addEventListener('touchstart', onStart, { passive: false });  // must be non-passive
el.addEventListener('touchmove',  onMove,  { passive: true });   // scroll perf matters here
el.addEventListener('touchend',   onEnd);
el.addEventListener('touchcancel', onEnd);
el.addEventListener('contextmenu', onContextMenu);
```

```html
<!-- CSS class applies -webkit-touch-callout: none; user-select: none -->
<img src="..." draggable="false" class="no-callout" />
```

## What doesn't work
- **Only CSS:** the callout still fires on some iOS/WebKit versions because it's a touch-gesture behavior, not purely image-element styling.
- **Only `preventDefault` at `click` time:** too late — the callout rendered during the touch, before any click event existed.
- **Putting `no-callout` on the wrapper `<div>` only:** the `<img>` is the touch target; Safari doesn't walk ancestors looking for the property.
- **Passive `touchstart` with `preventDefault` in the timer:** the browser silently ignores `preventDefault` on passive listeners. There is no console warning.
