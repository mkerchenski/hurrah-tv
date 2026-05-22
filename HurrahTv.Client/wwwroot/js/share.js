// share.js — share a URL via the Web Share API, falling back to clipboard.
// Loaded as an ES module via IJSObjectReference (see Services/ShareService.cs).
// This is the first JS module under wwwroot/js/; the broader migration off the
// inline <script> globals in index.html is tracked in issue #87.

export async function shareOrCopy({ title, text, url }) {
    const data = { title, text, url };
    if (navigator.share) {
        try {
            await navigator.share(data);
            return { outcome: 'shared' };
        } catch (e) {
            // any share rejection means the user already saw the sheet (or the API
            // refused mid-flight on iOS Safari with NotAllowedError, etc.) — DON'T
            // surprise them by silently overwriting their clipboard. Treat every
            // share-API rejection as cancelled. Clipboard fallback only kicks in
            // when navigator.share isn't present at all (e.g. desktop Chrome).
            return { outcome: 'cancelled' };
        }
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
        try {
            await navigator.clipboard.writeText(url);
            return { outcome: 'copied' };
        } catch {
            return { outcome: 'error' };
        }
    }
    return { outcome: 'unsupported' };
}
