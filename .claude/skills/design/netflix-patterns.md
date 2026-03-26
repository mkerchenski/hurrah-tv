# Netflix Design Patterns — Reference for Hurrah.tv

Hurrah.tv's design is loosely based on Netflix. This document captures the key patterns to reference when building new pages or components.

## Layout Patterns

### Home Page Structure
```
[Nav Bar - minimal, semi-transparent over content]
[Billboard Hero - large backdrop, title, description, action buttons]
[Content Row 1 - "Trending on Your Services"]
[Content Row 2 - "Popular on Netflix"]
[Content Row 3 - "Popular on Hulu"]
... more rows
[Footer - attribution, links]
```

### Content Row
- Category title on the left, "See All" link on the right
- Horizontal row of poster cards
- Cards are uniform size, tightly spaced
- On larger screens, show 6 posters per row
- Overflow hidden — suggests more content to scroll

### Billboard / Hero
- Full-width backdrop image with gradient overlay (bottom: solid dark, left: text-safe zone)
- Title large and bold over the backdrop
- Brief description (2-3 lines max, truncated)
- Action buttons: primary "Play" / secondary "More Info"
- Hurrah.tv equivalent: "Watch Now" / "Add to Queue" / "Details"

### Detail Page (Netflix "More Info" modal → our full page)
- Large backdrop across top
- Poster card overlapping on the left
- Title, year, rating, genre tags
- Overview/description
- "Available On" service badges
- Season selector (for TV)
- Episode list or similar content grid

## Visual Patterns

### Color Usage
- Background: Near-black (#0f0f0f to #141414)
- Cards/surfaces: Slightly lighter dark (#1a1a1a to #242424)
- Text: White for titles, gray for secondary, dark gray for muted
- Accent: Used sparingly for CTAs only (Netflix uses red, Hurrah.tv uses indigo)
- No colored backgrounds for sections — dark everywhere

### Hover Effects
- Poster cards scale up slightly (1.05x) on hover
- A subtle shadow/glow appears
- Metadata overlay fades in (title, year, rating, quick actions)
- Transition: 200ms ease for transform, 150ms for opacity

### Transitions & Motion
- Page content fades in on load (0.3s)
- Cards scale on hover (0.2s ease)
- Modals/overlays fade + slide (0.2s)
- No bounce, no spring — everything is smooth and subtle
- Loading: shimmer placeholders shaped like the content they replace

### Spacing
- Generous padding on page edges (px-6 minimum)
- Gap between cards: gap-4 (16px)
- Between sections: mt-10 (40px)
- Between section title and content: mb-4 (16px)
- Max content width: max-w-7xl centered

### Typography in Context
```
Billboard title:     text-4xl font-bold text-white
Section header:      text-xl font-bold text-white
Card title:          text-sm font-semibold text-white
Card metadata:       text-xs text-gray-400
Description:         text-sm text-gray-300 line-clamp-3
Badge/label:         text-xs uppercase tracking-wider text-gray-500
Action button:       text-sm font-semibold text-white
```

## Navigation
- Sticky top, becomes more opaque on scroll (Netflix behavior)
- Logo on the left (Netflix: red N, Hurrah.tv: "hurrah.tv" in accent)
- Few links: Home, Search, My Queue
- Utility icons on right: settings, profile/sign-out
- Mobile: hamburger or bottom tab bar

## Responsive Breakpoints

| Breakpoint | Columns | Poster behavior |
|------------|---------|-----------------|
| < sm (640) | 2 | Compact, no hover overlay |
| sm-md | 3 | Compact |
| md-lg | 4 | Standard |
| lg-xl | 5 | Standard |
| xl+ | 6 | Full hover effects |

## What NOT to Copy from Netflix
- Don't auto-play video previews on hover (distracting, resource-heavy)
- Don't use horizontal scroll carousels yet (grid is simpler for v1)
- Don't show "% Match" scores (we don't have recommendation data)
- Don't use red as the accent (that's Netflix's brand — we use indigo)
- Don't add profiles/avatars (single-user for now)

## Hurrah.tv Differentiators from Netflix
- **Multi-service** — show content from ALL user's services, not just one
- **Queue-first** — the watchlist is the core feature, not playback
- **Service badges** — every piece of content shows which services have it
- **Unified search** — search across all services simultaneously
