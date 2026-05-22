// share.js — share a URL via the Web Share API, falling back to clipboard.
// Loaded as an ES module via IJSObjectReference (see Services/ShareService.cs).
// This is the first JS module under wwwroot/js/; the broader migration off the
// inline <script> globals in index.html is tracked in issue #87.

// per issue #67 acceptance criteria: native share sheet on mobile, clipboard fallback elsewhere.
// navigator.share exists on desktop Chrome/Edge too, but we route those to clipboard so the
// 'tell a friend' flow on desktop drops a URL ready to paste into the user's chat of choice
// instead of opening the OS-level share UI that desktop users don't expect.
function isMobile() {
    if (navigator.userAgentData?.mobile !== undefined) return navigator.userAgentData.mobile;
    return /Mobi|Android|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);
}

export async function shareOrCopy({ title, text, url }) {
    if (navigator.share && isMobile()) {
        try {
            await navigator.share({ title, text, url });
            return { outcome: 'shared' };
        } catch (e) {
            // AbortError = user dismissed the sheet; NotAllowedError = iOS Safari's dismiss
            // path on some flows — both are silent cancellations, not failures. Anything else
            // (TypeError from bad payload shape, real permission failure) is a real error and
            // surfaces to the user via the caller's toast.
            if (e?.name === 'AbortError' || e?.name === 'NotAllowedError') {
                return { outcome: 'cancelled' };
            }
            return { outcome: 'error' };
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
