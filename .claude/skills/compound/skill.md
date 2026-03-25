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

### Step 2: Interview
Ask the user (one question at a time, conversationally):
1. "What was the trickiest part of this work?"
2. "Was there anything surprising or non-obvious?"
3. "Any gotchas future-you should know about?"
4. "Did we make any architectural decisions worth recording?"

Stop asking when the user indicates they're done.

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

## Context
[What situation led to this discovery]

## Learning
[The non-obvious insight — what to know and why it matters]

## Example
[Code snippet or concrete example if applicable]
```

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
