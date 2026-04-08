# Static Readonly Arrays in WASM Are Globally Mutable

> **Area:** WASM
> **Date:** 2026-04-08

## Context
`BadgeHelpers.AllStatuses` was initially declared as `public static readonly QueueStatus[]`. The `readonly` modifier prevents reassigning the reference, but not mutating the array elements. In a server context this would be a per-request risk; in Blazor WASM it's worse.

## Learning
Blazor WASM runs in a single long-lived browser process. There are no per-request resets, no AppDomain recycling, no worker process restarts. A `static readonly T[]` is effectively a singleton for the entire browser session — if any caller does `AllStatuses[0] = QueueStatus.NotForMe`, every component that iterates the array picks up the corruption immediately and for the rest of the session.

Use `IReadOnlyList<T>` for any shared static collection that multiple components consume:

```csharp
// wrong — elements are mutable despite readonly reference
public static readonly QueueStatus[] AllStatuses = [...];

// correct — elements are not settable
public static readonly IReadOnlyList<QueueStatus> AllStatuses = [...];
```

`ReadOnlySpan<T>` is safer still but cannot be stored as a static field. `IReadOnlyList<T>` is the idiomatic fix with zero allocation overhead.

## Why It Matters in WASM Specifically
On a server, even a corrupted static would be reset on the next deploy or process restart. In WASM the process lives until the user closes the tab — a corruption from one interaction propagates to every subsequent interaction in that session with no recovery path.
