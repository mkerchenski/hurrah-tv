// version.js — checks /api/version against the version observed on first call.
// Returns true once the deployed version changes (signal to show the UpdateBanner).
// Module-local state replaces the previous window._hurrahVersion global.
// Extracted from inline window.hurrahCheckVersion in index.html as part of issue #87.

let lastSeenVersion = null;

export async function check() {
    try {
        const res = await fetch('/api/version', { cache: 'no-store' });
        if (!res.ok) return false;
        const data = await res.json();
        if (!lastSeenVersion) { lastSeenVersion = data.version; return false; }
        return data.version !== lastSeenVersion;
    } catch { return false; }
}
