# Changelog

All notable changes to Hurrah.tv are documented here.

Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning follows dated entries rather than SemVer — this is a web app with continuous deploys, not a library with consumers pinning versions.

Types of changes: **Added**, **Changed**, **Fixed**, **Removed**, **Security**.

## [Unreleased]

## [2026-06-25]

### Added
- **Send Feedback** — report bugs or request features in-app (Settings → Help us improve)
- **What's Changed** page plus a new-feature banner that highlights updates you haven't seen yet

### Changed
- **Available Later** now surfaces upcoming episodes for any show on any service — previously limited to your subscribed services and the next 14 days

---

## Keeping this up to date

- When a PR ships a user-visible change, the PR body should add an entry under **[Unreleased]** — author writes it as part of the PR
- Roughly monthly, the **[Unreleased]** section is cut into a dated release section (e.g., `## [2026-05-01]`) and a new empty **[Unreleased]** is started
- Entries should describe *what changed from the user's perspective*, not implementation details — "Faster Details page" is better than "Removed full queue fetch in Details.razor"

Skip entries for: internal refactors, docs-only changes, tooling tweaks, dependency updates.
