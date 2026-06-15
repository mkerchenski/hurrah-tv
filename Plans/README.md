# Plans

Design documents and implementation plans for hurrah.tv. This directory is **tracked in git** so plans sync across machines and sit alongside the code they describe — mirroring the convention in the sibling Hurrah / Centralpoint repos.

hurrah.tv is a **public** repo, so the split below matters:

| Location | Tracked? | What goes here |
|---|---|---|
| `Plans/*.md` | **Yes (public)** | Non-sensitive technical design that maps to an **open** GitHub issue (or is a genuinely evergreen architecture record). Anything here is visible to the world. |
| `Plans/private/` | No (gitignored) | Sensitive or internal: product strategy, personal/onboarding docs, internal-process records, infra detail, and **`archive/`** (superseded plans for shipped work, kept for reference). |

## When to write a plan

`/xplan` (plan mode) writes plans here before substantial work. A plan earns a spot in the **public** `Plans/` only if both hold:

1. It maps to an **open** issue — put `**Tracking issue:** #NN` in the frontmatter, and add a `Related plan: Plans/<file>.md` line to that issue so the link is bidirectional.
2. It contains no secrets, infra hostnames, personal data, or competitive strategy. If it does, it belongs in `Plans/private/`.

When the work ships, mark the plan `Complete` (keep it only if it's still useful as a design record — e.g. background for a follow-up refactor issue); otherwise move it to `private/archive/` or delete it. Durable engineering insight from the work goes to `Learnings/` via `/compound`, not here.

## Plan format

```markdown
# Feature Name — Implementation Plan

> **Status:** Draft | Active | Complete | On Hold
> **Tracking issue:** #NN   (open issue this plan implements; required for public plans)

[numbered phases with checkable items — self-contained enough to paste into an issue]
```

## Lifecycle

`/xplan` → `Plans/` (before work, links an issue) → implement → `/compound` → `Learnings/` (durable insight) + memory (project context / preferences). Plans cross-reference the learnings that informed them; issues cross-reference the plans that implement them.
