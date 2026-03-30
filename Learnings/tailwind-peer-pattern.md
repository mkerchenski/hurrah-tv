# Tailwind `peer` Class Goes Only on the Source Element

> **Area:** UI
> **Date:** 2026-03-30

## Context
Built a CSS-only toggle switch for the Settings page using Tailwind's `peer` / `peer-checked:` pattern with a hidden checkbox.

## Learning
The `peer` class must appear **only** on the source element (the input/checkbox). The sibling elements that respond to state changes use `peer-checked:*` utilities but must NOT have the `peer` class themselves.

Tailwind generates CSS like:
```css
.peer:checked ~ .peer-checked\:bg-accent { background-color: ... }
```

If you put `peer` on a responder `div`, it becomes a potential source for future `peer-checked:` selectors, creating style conflicts when multiple toggles exist in the same container. More importantly, it's semantically wrong and can cause hard-to-debug CSS issues.

## Example
```html
<!-- WRONG: peer on the track div -->
<input type="checkbox" class="sr-only peer" />
<div class="peer peer-checked:bg-accent ..."></div>  <!-- BUG: peer here -->

<!-- CORRECT: peer only on input -->
<input type="checkbox" class="sr-only peer" />
<div class="peer-checked:bg-accent ..."></div>  <!-- responder only -->
<div class="peer-checked:translate-x-4 ..."></div>
```
