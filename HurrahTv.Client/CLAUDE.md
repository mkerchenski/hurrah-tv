# HurrahTv.Client — UI conventions

Applies to Blazor WASM UI work under `HurrahTv.Client/`. Repo-wide rules live in the root `CLAUDE.md`.

- Dark theme, poster-grid layout inspired by Netflix. Mobile bottom tab bar, desktop top nav.
- State lives on the server — client fetches on page load. No client-side state store.
- Prefer **self-gating predicates** over caller-supplied visibility flags. If a control's "show me" rule can be expressed from the item's own data (e.g. `status == Watching && latestEpisodeDate within 7 days`), encode it inside the component rather than passing a `showX` boolean from every call site. Self-gating keeps the rule canonical, makes new surfaces safe by default, and makes the intent grep-able.
