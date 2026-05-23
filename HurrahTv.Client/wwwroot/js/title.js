// title.js — apply an environment prefix to document.title so dev/staging tabs
// are visually distinguishable from prod. Replaces a JS.InvokeVoidAsync("eval", ...)
// call as part of issue #87 AC #4.

export function applyEnvPrefix(prefix) {
    if (!prefix) return;
    if (!document.title.startsWith(prefix)) {
        document.title = prefix + document.title;
    }
}
