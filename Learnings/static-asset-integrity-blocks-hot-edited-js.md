# Hot-Editing a wwwroot JS File Breaks Its Integrity Hash — the Browser Blocks It

> **Area:** WASM | Deployment
> **Date:** 2026-06-09

## Context
Debugging "drag-reorder doesn't work" on the Queue page, the drag behaved differently on every attempt — didn't work, then worked, then "grabs but nothing moves." A console diagnostic (run in a clean DevTools-controlled Chrome) showed `window.Sortable` loaded fine and the ESM module imported fine, yet `Sortable.get(listEl)` returned `null` — the instance never attached. The console held the real cause:

```
Failed to find a valid digest in the 'integrity' attribute for resource
'https://localhost:7267/js/sortable.bcodwy7q8b.js' with computed SHA-256
integrity 'MmBznFLzD5Kb4nU2yjjPHwuj/q8VnbktjFEh2ILVvws='. The resource has been blocked.
```

The "nothing moves" only started **after** I added a temporary `console.log` to `sortable.js` to debug — so I wrongly told the user the log couldn't affect dragging. It could, via this mechanism.

## Learning
Blazor WASM fingerprints static assets and bakes a **Subresource Integrity (SRI)** hash for each into the import map (`<script type="importmap">` / `blazor.boot`). `dotnet watch` hot-reload does **NOT** re-fingerprint static JS under `wwwroot/` — it serves the edited file, but the import map still carries the *old* content's integrity hash. The browser computes the new file's hash, finds the mismatch, and **blocks the module entirely**. Any JS-interop module import (`import('./js/sortable.js')`) silently fails, so whatever it powers (here, SortableJS attaching to the list) just never happens — no error in the C# side, only a console SRI error.

Consequences:
- You **cannot** instrument a fingerprinted `wwwroot/*.js` file by editing it at runtime — the edit disables it. Capture runtime data by wrapping the live object via DevTools `evaluate` instead (e.g. override `inst.options.onEnd`), not by editing the file.
- A full client **rebuild** (restart `dotnet watch` for the Client, or `dotnet build`) regenerates the import map with the new integrity hash; only then does the edited file load.
- This compounds with a stale PWA service worker serving old vs. new assets — together they make a hot-edited JS feature behave erratically across reloads.

Distinct from [[dotnet-watch-locks-shared-dll.md]] (a Windows DLL file-lock during `dotnet test`); this is a browser-side integrity block on static web assets.

## Example
```javascript
// DON'T: edit wwwroot/js/sortable.js to add a debug log — integrity mismatch blocks the whole module.
// DO: capture runtime data without touching the file —
const inst = window.Sortable.get(document.querySelector('[data-item-id]').parentElement);
const original = inst.options.onEnd;
inst.options.onEnd = (evt) => { console.log(evt.oldDraggableIndex, evt.newDraggableIndex); return original(evt); };
```
After a genuine fix to the file, the served `?` build must re-fingerprint before the browser will load it — restart the Client `dotnet watch` (ask the dev to do it; don't force-restart their session).
