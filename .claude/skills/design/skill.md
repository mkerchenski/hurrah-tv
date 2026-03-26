---
description: Apply design thinking to Hurrah.tv UI — Netflix-inspired dark theme streaming app aesthetic with Tailwind CSS
user-invocable: true
---

# /design — Design System for Hurrah.tv

Automatically applies when creating or modifying visual elements (pages, components, layouts).

## Design Identity

Hurrah.tv is a **consumer streaming app**, not an enterprise tool. The design is **loosely based on Netflix** — immersive, content-forward, dark, with minimal chrome. Users should feel instantly familiar with the layout and interactions.

### Netflix-Inspired Patterns
- **Billboard/hero area** at top of home page — large backdrop with title overlay
- **Horizontal content rows** with category headers ("Trending on Your Services", "Popular on Netflix")
- **Poster-dominant layout** — the artwork IS the UI, text is secondary
- **Hover-to-reveal** — metadata, actions, and details appear on poster hover
- **Minimal navigation** — top bar with logo left, few links right, search icon
- **Dark everywhere** — near-black backgrounds, no white surfaces
- **Subtle depth** — cards lift on hover, sections separated by spacing not borders

### Principles
1. **Content is king** — Posters, backdrops, and metadata ARE the design. UI chrome should be invisible.
2. **Hierarchy over harmony** — One hero element per view, everything else recedes.
3. **Reduce until it breaks** — Start by removing elements, not adding them.
4. **Dark-first** — Dark theme is the primary experience, not an afterthought.
5. **Sweat the details** — Spacing, transitions, hover states, loading states all matter.

## Color System

```
Surface:     #0f0f0f (bg) → #1a1a1a (50) → #242424 (100) → #2e2e2e (200)
Accent:      #6366f1 (indigo-500) → #818cf8 (light)
Text:        gray-100 (primary) → gray-300 (secondary) → gray-500 (muted) → gray-600 (disabled)
Status:      green-500 (success) → red-500 (error) → yellow-400 (star/rating)
Service colors: Netflix #E50914, Prime #00A8E1, Hulu #1CE783, Disney+ #113CCF, etc.
```

Tailwind custom colors are configured via CDN `tailwind.config` in `index.html`:
- `bg-surface`, `bg-surface-50`, `bg-surface-100`, `bg-surface-200`
- `text-accent`, `text-accent-light`, `bg-accent`, `bg-accent-light`

## Typography
- Font: Inter (system fallback: -apple-system, Segoe UI, Roboto)
- Hero: 4xl bold (onboarding title)
- Page title: 2xl bold (section headers)
- Card title: sm semibold (poster overlay)
- Body: base/sm (descriptions, metadata)
- Label: xs uppercase tracking-wider (section labels like "AVAILABLE ON")

## Icons — Heroicons

Use **Heroicons** (by the Tailwind team) for all icons. The app uses inline SVGs from the Heroicons set.

### Usage Pattern
```html
<!-- outline style (24x24), used for nav and UI actions -->
<svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="..." />
</svg>

<!-- solid style (20x20), used for filled states like active nav -->
<svg class="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
    <path fill-rule="evenodd" d="..." clip-rule="evenodd" />
</svg>
```

### Current Icons in Use
- **Search** (magnifying glass) — nav bar
- **Queue/list** (horizontal lines) — nav bar "My Queue"
- **Settings** (gear/cog) — nav bar
- **Sign out** (arrow right from box) — nav bar
- **Plus** — add to queue
- **Check** — in queue / watched
- **Star** — ratings

### Guidelines
- Use **outline** style (stroke) for inactive/default states
- Use **solid** style (fill) for active/selected states
- Size: `w-5 h-5` for nav icons, `w-4 h-4` for inline/card icons, `w-6 h-6` for hero actions
- Color inherits from parent via `currentColor` — don't hardcode colors on icons
- Browse full set at https://heroicons.com — copy SVG path data only

## Existing Components

### PosterCard (`Components/PosterCard.razor`)
- 2:3 aspect ratio, rounded-lg, overflow-hidden
- Hover: scale(1.05) + indigo glow shadow + gradient overlay with metadata
- Quick-add button appears top-right on hover
- Loading state: shimmer animation placeholder
- Props: SearchResult, compact mode

### PosterGrid (`Components/PosterGrid.razor`)
- Responsive grid: 6 columns on xl, down to 2 on mobile
- Accepts list of SearchResult + loading state + empty message
- gap-4 between cards

### ServicePicker (`Components/ServicePicker.razor`)
- Pill/badge toggles for each streaming service
- Selected state: accent border + opacity change
- Used in onboarding and settings

### Section Layout Pattern
- Section header: bold title left, action link right
- Spacing: gap-4 between cards, mt-10 between sections

### Detail Page (`Pages/Details.razor`)
- Full-width backdrop (40% opacity) with gradient fade to surface
- Poster overlapping backdrop on left, metadata on right
- Service badges with logos in "AVAILABLE ON" section
- Season grid below

### Navigation (`Layout/MainLayout.razor`)
- Sticky top bar, surface-50 background with subtle border
- Logo left (text "hurrah.tv" in accent), links right
- Minimal — Search, My Queue, Settings gear, Sign out

## Quick Wins Checklist
When reviewing any visual output:
- [ ] Does it feel like Netflix? Dark, content-forward, poster-dominant?
- [ ] Does the content (posters, images) dominate over UI chrome?
- [ ] Is there one clear focal point per view?
- [ ] Are transitions smooth (0.2s ease for transforms, 0.15s for opacity)?
- [ ] Do loading states use shimmer, not spinners (except initial page load)?
- [ ] Is spacing generous? (When in doubt, add more padding)
- [ ] Are interactive elements discoverable via hover states?
- [ ] Does text have sufficient contrast against dark backgrounds?
- [ ] Are icons from Heroicons, using the outline/solid convention?

## Elevation Protocol
When polishing a page:
1. **Typography pass** — Size hierarchy clear? One display size, rest body.
2. **Color pass** — Only accent color for actions, gray for everything else.
3. **Spacing pass** — Generous whitespace, consistent gap sizes.
4. **Transition pass** — Hover/focus states on all interactive elements.
5. **Content pass** — Real data looks good? Empty states handled?
6. **Icon pass** — All icons from Heroicons? Consistent sizing and style?

## References
- **Primary inspiration:** Netflix (layout, interactions, dark aesthetic, content rows)
- **Secondary:** Spotify (dark UI, minimal chrome), Letterboxd (poster-forward)
- **Avoid:** Cable TV guide aesthetic, enterprise dashboard feel, bright/cluttered layouts

## Supporting Files
- [heroicons.md](heroicons.md) — Full icon reference with SVG paths, sizing, and templates
- [netflix-patterns.md](netflix-patterns.md) — Netflix layout patterns, responsive breakpoints, and what to copy vs. skip
