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
            // user dismissed the share sheet — not an error, just no-op
            if (e && e.name === 'AbortError') return { outcome: 'cancelled' };
            // any other share failure (permission, abort race) falls through to clipboard
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
