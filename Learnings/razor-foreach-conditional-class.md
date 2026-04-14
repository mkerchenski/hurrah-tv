# Ternary Class Strings in Razor @foreach Silently Fail

> **Area:** WASM | UI
> **Date:** 2026-04-14

## Context
Built a sort toggle (Date / Liked / List) for watchlist rows. Used a Tailwind conditional class expression inside a `@foreach` loop. Despite the sort value being correct (sorting was working), the active button never appeared highlighted — all three buttons rendered identically regardless of which was selected.

## Learning
**`@(condition ? "classA" : "classB")` inside a Razor `@foreach` block does not reliably apply conditional CSS classes.** The issue is how Blazor's code generator handles code/markup transitions inside foreach — the ternary expression evaluates correctly in C# but the resulting class string isn't always applied as Blazor expects when the parent is a `@foreach` rendering multiple elements of the same shape.

**The reliable pattern is `@if/@else` blocks that render two distinct `<button>` elements**, not one button whose class changes:

```razor
<!-- BAD — conditional class silently does not apply -->
@foreach (string mode in new[] { "date", "sentiment", "queue" })
{
    bool active = SortMode == mode;
    <button class="@(active ? "bg-surface-200 text-white" : "text-gray-500")">
        @mode
    </button>
}

<!-- GOOD — two distinct elements, Blazor picks one -->
@foreach (string mode in new[] { "date", "sentiment", "queue" })
{
    @if (SortMode == mode)
    {
        <button class="bg-surface-200 text-white font-semibold">@mode</button>
    }
    else
    {
        <button class="text-gray-500 hover:text-white">@mode</button>
    }
}
```

This also benefits Tailwind: when classes appear in full static strings rather than constructed expressions, Tailwind's scanner reliably includes them in the output bundle. Conditional string assembly (`condition ? "classA" : "classB"`) can cause classes to be missing from the CSS if they don't appear elsewhere in the codebase.

## Note
The ternary pattern works fine *outside* of `@foreach` loops (e.g., on a standalone element). The failure is specific to the combination of `@foreach` + multiple same-shape elements + ternary class expression.
