---
description: Apply design thinking to Hurrah.tv UI — dark theme streaming app aesthetic with Tailwind CSS
user-invocable: true
---

# /design — Design System for Hurrah.tv

Automatically applies when creating or modifying visual elements (pages, components, layouts).

## Design Identity

Hurrah.tv is a **consumer streaming app**, not an enterprise tool. The aesthetic should feel like browsing Netflix or Spotify — immersive, content-forward, and minimal chrome.

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

## Typography
- Font: Inter (system fallback: -apple-system, Segoe UI, Roboto)
- Hero: 4xl bold (onboarding title)
- Page title: 2xl bold (section headers)
- Card title: sm semibold (poster overlay)
- Body: base/sm (descriptions, metadata)
- Label: xs uppercase tracking-wider (section labels like "AVAILABLE ON")

## Component Patterns

### Poster Card
- 2:3 aspect ratio, rounded-lg, overflow-hidden
- Hover: scale(1.05) + indigo glow shadow + gradient overlay with metadata
- Quick-add button appears top-right on hover
- Loading state: shimmer animation placeholder

### Section Layout
- Full-width poster grid: 6 columns on xl, responsive down to 2
- Section header: bold title left, action link right
- Spacing: gap-4 between cards, mt-10 between sections

### Detail Page
- Full-width backdrop (40% opacity) with gradient fade to surface
- Poster overlapping backdrop on left, metadata on right
- Service badges with logos in "AVAILABLE ON" section
- Season grid below

### Navigation
- Sticky top bar, surface-50 background with subtle border
- Logo left, links right
- Minimal — just logo, Search, My Queue, Settings gear

## Quick Wins Checklist
When reviewing any visual output:
- [ ] Does the content (posters, images) dominate over UI chrome?
- [ ] Is there one clear focal point per view?
- [ ] Are transitions smooth (0.2s ease for transforms, 0.15s for opacity)?
- [ ] Do loading states use shimmer, not spinners (except initial page load)?
- [ ] Is spacing generous? (When in doubt, add more padding)
- [ ] Are interactive elements discoverable via hover states?
- [ ] Does text have sufficient contrast against dark backgrounds?

## Elevation Protocol
When polishing a page:
1. **Typography pass** — Size hierarchy clear? One display size, rest body.
2. **Color pass** — Only accent color for actions, gray for everything else.
3. **Spacing pass** — Generous whitespace, consistent gap sizes.
4. **Transition pass** — Hover/focus states on all interactive elements.
5. **Content pass** — Real data looks good? Empty states handled?

## References
- **Admired:** Netflix, Spotify, Letterboxd (poster-forward dark UIs)
- **Avoid:** Cable TV guide aesthetic, enterprise dashboard feel, bright/cluttered layouts
