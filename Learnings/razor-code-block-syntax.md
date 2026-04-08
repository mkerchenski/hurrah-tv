# Razor @{} Is Invalid Inside Existing Code Blocks

> **Area:** WASM
> **Date:** 2026-04-08

## Context
Tried to declare a local variable inside an `@else` block using `@{ List<QueueItem> visible = FilteredItems.ToList(); }`. This caused build error `RZ1010: Unexpected "{" after "@" character`.

## Learning
In Razor, the `@` prefix means "switch from markup to code here." Inside an `@if`, `@else`, `@foreach`, or any other code block, you are already in code — the `@` prefix is unnecessary and invalid:

```razor
@* WRONG — double-transition causes RZ1010 *@
else
{
    @{
        List<QueueItem> visible = FilteredItems.ToList();
    }
    @if (!visible.Any()) { ... }
}

@* CORRECT — already in code, declare directly *@
else
{
    List<QueueItem> visible = FilteredItems.ToList();

    @if (!visible.Any()) { ... }
}
```

The `@{...}` standalone block is only valid at the **markup level** (top of a component, between HTML elements) where Razor needs an explicit signal to enter code mode. Inside an existing code block, write C# directly.

The error message (`Unexpected "{"`) is misleading — the real problem is the `@`, not the `{`.
