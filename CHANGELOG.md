# Changelog

All notable changes to Hurrah.tv are documented here.

Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning follows dated entries rather than SemVer — this is a web app with continuous deploys, not a library with consumers pinning versions.

Types of changes: **Added**, **Changed**, **Fixed**, **Removed**, **Security**.

## [Unreleased]

### Added
- Contributor onboarding: `CONTRIBUTING.md`, `Hurrah.tv.code-workspace`, issue + PR templates, label taxonomy, `/review` skill now files unaddressed findings as issues
- 24 GitHub issues migrated from the former Trello board (bugs, enhancements, future items)

### Changed
- Line endings normalized across contributors via `.gitattributes` (LF in repo, native on checkout)

---

## Keeping this up to date

- When a PR ships a user-visible change, the PR body should add an entry under **[Unreleased]** — author writes it as part of the PR
- Roughly monthly, the **[Unreleased]** section is cut into a dated release section (e.g., `## [2026-05-01]`) and a new empty **[Unreleased]** is started
- Entries should describe *what changed from the user's perspective*, not implementation details — "Faster Details page" is better than "Removed full queue fetch in Details.razor"

Skip entries for: internal refactors, docs-only changes, tooling tweaks, dependency updates.
