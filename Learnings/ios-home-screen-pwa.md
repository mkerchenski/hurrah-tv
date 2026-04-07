# iOS "Add to Home Screen" — No Service Worker Needed, But Full of Gotchas

> **Area:** UI | WASM | Deployment
> **Date:** 2026-04-07

## Context
Wanted to let iOS users install Hurrah.tv as a home screen app. Investigated whether we needed a full PWA with service worker. Built an install-prompt banner for iOS Safari since there's no programmatic install API.

## Learning
iOS Safari supports "Add to Home Screen" with just a web app manifest (`display: standalone`) and an `apple-touch-icon`. No service worker required for the basic install experience. The app opens fullscreen without Safari chrome.

However, Safari doesn't support the `beforeinstallprompt` event that Android Chrome uses, so you can't programmatically trigger the install prompt. The only option is a dismissible banner that shows the user how to do it manually (tap Share > "Add to Home Screen").

**Gotchas discovered during implementation and review:**

1. **`localStorage` throws in iOS Safari private browsing** — older iOS versions throw `SecurityError` on any `localStorage` access (reads too, not just writes). Since the banner targets iOS Safari users, this is the exact population that hits it. Wrap all `localStorage` calls in try/catch.

2. **Apple Silicon Macs match the iPadOS heuristic** — `navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1` was the standard way to detect iPadOS (which reports as macOS). But M-series Macs also return `MacIntel` with `maxTouchPoints = 5`. The banner incorrectly shows on desktop Safari. No perfect fix yet — `navigator.platform` is deprecated and `navigator.userAgentData` isn't supported by Safari.

3. **`navigator.platform` is deprecated** — modern browsers are freezing this value. It still works in Safari today but will become less reliable over time.

4. **Blazor JS interop in lifecycle methods needs try/catch** — if the JS function throws (broken browser API, localStorage error), the exception propagates as unhandled from `OnAfterRenderAsync`, triggering `blazor-error-ui`. Always wrap JS interop in try/catch in lifecycle methods.

5. **`display-mode: standalone` media query** detects if the app was launched from the home screen. Use this to suppress the install banner for users who already installed.

## Example
```javascript
// safe iOS detection with localStorage guard
window.hurrahShouldShowInstallBanner = () => {
    try {
        if (window.matchMedia('(display-mode: standalone)').matches) return false;
        if (navigator.standalone) return false; // Safari-specific
        if (localStorage.getItem('dismissed-key')) return false;
        const ua = navigator.userAgent;
        const isIos = /iPad|iPhone|iPod/.test(ua)
            || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
        const isSafari = /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS|OPiOS/.test(ua);
        return isIos && isSafari;
    } catch { return false; }
};
```
