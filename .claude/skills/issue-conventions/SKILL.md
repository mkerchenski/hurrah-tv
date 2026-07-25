---
name: issue-conventions
description: Label scheme and issue body template for GitHub Issues on mkerchenski/hurrah-tv. Use when creating, labeling, or triaging an issue in this repo — the label scheme is the tracker (there's no project board), and new issues need at least one area: label plus a phase: label.
---

# Issue conventions — Hurrah.tv

Issues live in **GitHub Issues** on [`mkerchenski/hurrah-tv`](https://github.com/mkerchenski/hurrah-tv/issues). There's no project board — the label scheme is the tracker (`gh issue list --label "phase:now"` is the "In Progress" view).

## Label conventions

Already installed; visible via `gh label list --repo mkerchenski/hurrah-tv`.

| Dimension | Prefix | Values |
|---|---|---|
| Type | `type:` | `bug`, `feature`, `enhancement`, `chore`, `refactor`, `docs` |
| Area | `area:` | `api`, `client`, `auth`, `ai-curation`, `tmdb`, `design`, `docs`, `infra` |
| Difficulty | `difficulty:` | `starter`, `intermediate`, `advanced` |
| Phase | `phase:` | `now`, `next`, `future` |
| Bare state | — | `bug`, `enhancement`, `good first issue`, `help wanted`, `wontfix`, `duplicate` |

There's no `priority:*` scheme — `phase:*` does the work. There's no `effort:*` scheme — `difficulty:*` does the work.

## When skills create issues

- Default new issues to `phase:next` (Backlog equivalent). Use `phase:now` only if the user is about to act on it.
- Always set at least one `area:*`. Skills writing API or Client code should match the architectural slice.
- `from:audit` / `from:sentry` labels do NOT exist in this repo — use the body's "Surfaced by:" footer instead.

## Issue body shape

Used by all skills:

```markdown
## What
<one-line summary>

## Why
<motivation, evidence, or the workflow that surfaced it>

## Acceptance criteria
- [ ] <measurable outcome 1>
- [ ] <measurable outcome 2>

Surfaced by: /<skill> on YYYY-MM-DD
```

## Closing the loop

Put `Closes #NN` / `Fixes #NN` keywords in the **PR description** (one per line at the bottom) — GitHub auto-closes the issue on merge to main. Squash-merge discards individual commit messages and replaces them with the PR title + body, so closes-keywords in commit bodies alone won't survive — the PR description is the canonical place. Don't manually close issues that the merge will close for you.
