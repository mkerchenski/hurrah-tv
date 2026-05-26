// install.js — iOS Safari PWA install banner: detect whether to prompt the user,
// and persist dismissal. Extracted from inline window.hurrah* helpers in index.html
// as part of issue #87.

const DISMISS_KEY = 'hurrah-install-dismissed';

export function shouldShow() {
    try {
        if (window.matchMedia('(display-mode: standalone)').matches) return false;
        if (navigator.standalone) return false;
        if (localStorage.getItem(DISMISS_KEY)) return false;
        const ua = navigator.userAgent;
        const isIos = /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
        const isSafari = /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS|OPiOS/.test(ua);
        return isIos && isSafari;
    } catch { return false; }
}

export function dismiss() {
    try { localStorage.setItem(DISMISS_KEY, '1'); } catch { }
}

// Android / desktop Chrome path: the deferred beforeinstallprompt event is captured
// early in index.html (it fires before this module loads) and stashed on window.
// canPromptInstall reports whether a programmatic install prompt is available.
export function canPromptInstall() {
    try {
        if (localStorage.getItem(DISMISS_KEY)) return false;
    } catch { /* private mode — fall through, still allow install */ }
    return !!window.__hurrahInstallPrompt;
}

// register a .NET callback fired when an install prompt becomes available — lets the
// banner appear without a reload if beforeinstallprompt arrives after first render.
// Fires immediately if the event was already captured before the component subscribed.
export function onInstallAvailable(dotNetRef) {
    window.__hurrahNotifyInstall = () => dotNetRef.invokeMethodAsync('OnInstallAvailable').catch(() => { });
    if (window.__hurrahInstallPrompt) window.__hurrahNotifyInstall();
}

// fire the native install prompt. The event is single-use, so clear it afterward —
// Chrome may emit a fresh beforeinstallprompt later if the user didn't install.
export async function promptInstall() {
    const evt = window.__hurrahInstallPrompt;
    if (!evt) return false;
    window.__hurrahInstallPrompt = null;
    try {
        evt.prompt();
        const choice = await evt.userChoice;
        return choice && choice.outcome === 'accepted';
    } catch {
        return false;
    }
}
