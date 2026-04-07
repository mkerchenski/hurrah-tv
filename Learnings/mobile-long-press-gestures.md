# Mobile Long-Press: Gesture Conflicts with Scroll and Tap Navigation

> **Area:** UI | WASM
> **Date:** 2026-04-07

## Context
Added long-press context menu to poster cards in horizontal scroll rows. Cards already have tap-to-navigate and the rows have horizontal scroll. All three gestures — tap, scroll, long-press — compete for the same touch events.

## Learning
The hardest part of implementing long-press on mobile web isn't the timer — it's preventing three gesture conflicts:

1. **Scroll vs long-press**: Horizontal scrolling moves the finger >10px, which should cancel the long-press timer. Without this, every scroll attempt opens the context menu.

2. **Tap vs long-press**: A normal tap (touch + release within 500ms) should navigate to details. The long-press timer must be cancelled on `touchend` so the `@onclick` handler fires normally.

3. **Long-press vs tap navigation**: After the 500ms timer fires, the browser still generates a synthetic `click` event from the touch sequence. This click must be suppressed so it doesn't navigate to the details page. The trick: register a `click` listener with `{ capture: true }` that checks a `fired` flag and calls `stopPropagation` + `preventDefault` only when the long-press actually triggered.

**Why this must be in JS, not Blazor**: Blazor's `@ontouchstart`/`@ontouchend` events don't give fine-grained control over `preventDefault` timing. The click suppression requires a capturing listener registered at the element level, which can only be done via `addEventListener` in JS.

**Why `{ passive: true }` on touchstart/touchmove**: iOS Safari requires passive touch listeners for scroll performance. Since we don't call `preventDefault` on touches (only on the synthetic click), passive is correct.

## Example
```javascript
// key pattern: timer + movement threshold + selective click suppression
const onStart = (e) => {
    const t = e.touches[0];
    startX = t.clientX; startY = t.clientY; fired = false;
    timer = setTimeout(() => {
        fired = true;
        dotNetRef.invokeMethodAsync(callback, itemId);
    }, 500);
};
const onMove = (e) => {
    if (!timer) return;
    const t = e.touches[0];
    // cancel if finger moved more than 10px (scrolling)
    if (Math.abs(t.clientX - startX) > 10 || Math.abs(t.clientY - startY) > 10)
        clearTimeout(timer);
};
const onEnd = () => clearTimeout(timer); // cancel on release (normal tap)
const onClick = (e) => {
    // suppress only the click that follows a long-press
    if (fired) { e.stopPropagation(); e.preventDefault(); fired = false; }
};
el.addEventListener('click', onClick, true); // capture phase
```
