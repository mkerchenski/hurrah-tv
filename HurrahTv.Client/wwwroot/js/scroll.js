// scroll.js — smooth horizontal scroll on row containers (ContentRow, WatchlistRow).
// Extracted from inline window.hurrahScrollRow in index.html as part of issue #87.

export function scrollRow(el, delta) {
    if (el) el.scrollBy({ left: delta, behavior: 'smooth' });
}
