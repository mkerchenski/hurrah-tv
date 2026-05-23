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
