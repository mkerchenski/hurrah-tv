namespace HurrahTv.Shared.Models;

// a parsed CHANGELOG.md release (#19). Version is the bracketed header text — a date like
// "2026-05-01" or "Unreleased". Sections group items under a Keep-a-Changelog category.
public record ChangelogEntry(string Version, IReadOnlyList<ChangelogSection> Sections);

// one category block within a release — e.g. Category "Added" with its bullet items.
public record ChangelogSection(string Category, IReadOnlyList<string> Items);
