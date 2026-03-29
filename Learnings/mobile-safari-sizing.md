# Mobile Safari Sizing: Don't Trust the Emulator

> **Area:** UI | WASM
> **Date:** 2026-03-29

## Context
Poster cards in horizontal scroll rows looked fine in Edge's device emulator but were way too large on a real iPhone. User reported a single poster taking up almost the entire screen width.

## Learning
Edge DevTools' device emulator does NOT accurately replicate Safari's viewport handling. Real iPhone Safari can render elements larger than expected, especially when:
- Only two breakpoints exist (mobile default + `md:`)
- `viewport-fit` isn't set to `cover` for notched phones
- There's no intermediate breakpoint between phone and tablet

**Fix: Use progressive sizing with 3 breakpoints:**
```
w-24      → 96px  (small phones, <640px)
sm:w-28   → 112px (large phones, ≥640px)
md:w-36   → 144px (tablets+, ≥768px)
```

Also add `viewport-fit=cover` to the viewport meta tag for proper Safari rendering on notched devices:
```html
<meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover" />
```

## Example
The `sm:` breakpoint at 640px is key — it catches the gap between small phones (375px iPhone SE) and tablets (768px+). Without it, phones jump straight from tiny to large posters with no intermediate size. Always test on a real device when sizing matters.
