---
description: Capture learnings after completing work — patterns, gotchas, and decisions that make future work easier
user-invocable: true
---

# /compound — Capture Engineering Learnings

Reflect on what was just accomplished and capture non-obvious discoveries.

## Process

### Step 1: Review What Changed
- Check `git diff` and `git log` for recent changes
- Understand what was built, fixed, or refactored

### Step 2: Draft Learnings Autonomously
Review the changes and identify learnings yourself. Don't interview the user question-by-question — instead, draft a table of proposed learnings with destinations and present it for approval in one shot. The user can approve, modify, or reject items.

### Step 3: Route to Destination
For each learning, decide where it belongs:

| Type | Destination |
|------|-------------|
| Non-obvious discovery about WASM, TMDb, streaming APIs | `Learnings/<topic>.md` |
| Reusable pattern or convention | `CLAUDE.md` update |
| User preference or workflow | Memory file |
| Future work or idea | `Plans/` note |

### Step 4: Write Learnings
Format each learning file:

```markdown
# Title

> **Area:** WASM | API | Data | TMDb | Auth | UI | Deployment
> **Date:** YYYY-MM-DD
> **Resolves:** mkerchenski/hurrah-tv#NN  (only when an issue triggered this learning — see below)

## Context
[What situation led to this discovery]

## Learning
[The non-obvious insight — what to know and why it matters]

## Example
[Code snippet or concrete example if applicable]
```

**Cross-reference resolved issues** — when a learning came out of debugging or implementing a tracked issue, link it from the frontmatter so future readers can trace context. Scan recent commits for issue references:

```bash
git log --grep='#' -20 --oneline
```

If a recent commit closed/fixed an issue and that issue surfaced this learning, add the `> **Resolves:** mkerchenski/hurrah-tv#NN` line. Opt-in only — don't fabricate links.

### Step 5: Deduplicate
Before writing, check:
- Existing `Learnings/` files for overlap
- `CLAUDE.md` for already-documented patterns
- Recent git history (if the fix is in the code, the learning may not need a file)

### Step 6: Summarize
Present a table of what was captured:

| Learning | Saved To | File |
|----------|----------|------|
| ... | Learnings/ | `<filename>.md` |

## Rules
- One learning per file, named descriptively
- Learnings/ is gitignored — local knowledge base
- Don't duplicate what's derivable from reading current code
- Focus on the *why*, not the *what*
