# Visual-Only Controls: Use `aria-hidden`, Not `<button tabindex="-1">`

> **Area:** UI | Accessibility
> **Date:** 2026-05-07

## Context

The drag handle on the queue's WantToWatch tab is mouse/touch-only at v1 — keyboard reorder is deferred. The first implementation rendered it as:

```html
<button class="drag-handle ..." aria-label="Reorder @item.Title" tabindex="-1">
    <span class="icon-[heroicons--bars-3] w-5 h-5 block"></span>
</button>
```

Reasoning at the time: "It's a button-like affordance, give it a button element and an aria-label so screen readers know what it is." Surfaced by a Copilot review on PR #65 as an a11y regression.

## Learning

`<button tabindex="-1" aria-label="Reorder ...">` is worst-of-both-worlds:

- The `<button>` element + `aria-label` advertise an interactive, labeled control to assistive tech.
- The `tabindex="-1"` removes it from the keyboard tab order.
- Result: AT users hear "Reorder Show Title, button" but cannot reach or activate it. There is no keyboard equivalent.

This is worse than no a11y surface at all. AT users can perceive a feature exists but discover they can't use it.

### Two correct alternatives

**Option A — visual-only affordance, no a11y surface:**

```html
<div class="drag-handle ..." aria-hidden="true">
    <span class="icon-[heroicons--bars-3] w-5 h-5 block"></span>
</div>
```

Use this when the gesture is genuinely mouse/touch-only at v1 and a keyboard equivalent will be added in a follow-on. AT users see the rest of the row (title, status badge, remove button) and use those — they don't get drag-reorder, but they aren't promised something they can't have.

**Option B — full keyboard support:**

```html
<button class="drag-handle ..." aria-label="Reorder @item.Title"
        @onkeydown="HandleReorderKey">
    <span class="icon-[heroicons--bars-3] w-5 h-5 block"></span>
</button>
```

With Up/Down arrows handled in C# to call the same reorder JSInvokable, plus an `aria-live="polite"` region announcing each move. This is the "do it right" path; ship it when the time investment matches the priority.

### When you might be tempted to use the broken pattern

- "I want the visual treatment a button gives me." Use `role="presentation"` on a `<div>` and style it however you like.
- "I want a focusable element so my CSS focus state highlights it for sighted keyboard users." If sighted keyboard users genuinely benefit, you owe AT users actual keyboard support — go to Option B.
- "I'll add keyboard support later and want the aria-label in place for when I do." File the follow-on; remove the misleading semantics now. A correct future state doesn't excuse a broken current state.

## Generalization

For any control: the element type and ARIA attributes you choose are a **promise** of how AT can interact with it. `<button>`, `<a>`, `role="button"`, `aria-label` — these all say "I am operable." If the control isn't actually operable in the AT user's input modality, those attributes lie.

The honest representation is: **a visual element with `aria-hidden="true"`** (the AT user gets nothing, but is told nothing) — *or* **a real interactive control with real keyboard support** (the AT user gets the same feature). Anything in between is a lie.

## File pointer

`HurrahTv.Client/Pages/Queue.razor` — drag handle row uses `<div aria-hidden="true">`. Keyboard support tracked as issue #66.
