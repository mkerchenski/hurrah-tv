---
description: Structured implementation planning — brainstorm, scope, and phase a feature before building
user-invocable: true
---

# /plan — Implementation Planning

## Philosophy
Spend 80% on planning, 20% on execution. Every non-trivial feature gets a plan before code.

## Mode Selection

Before doing any research, ask the user which planning mode they want:

> **Which planning mode?**
>
> - **ultraplan** — cloud session with collaboration tools (comments, diagrams, 2x faster). Requires GitHub connected at claude.ai/code.
> - **plan** — full structured plan written locally to `Plans/<name>.md`
> - **quick** — lightweight inline checklist for simple tasks (no file saved)

Wait for the user to respond before proceeding.

**If the user selects `ultraplan`**, ask a follow-up:

> Have you already connected your GitHub account at claude.ai/code?
>
> - **Yes** → proceed to the research steps below, then hand off to ultraplan
> - **No / Not sure** → show these setup steps first, then wait for confirmation:
>   1. Go to **claude.ai/code** in your browser and sign in
>   2. Find GitHub integration in account/settings and authorize the OAuth connection
>   3. If the repo is under a GitHub org, the org owner may need to approve the third-party app separately
>   4. Once connected, `/ultraplan` will clone the repo from GitHub rather than uploading local files
>   5. Confirm when done, then we'll proceed

**If the user selects `quick`**, skip to Quick Mode below.

---

## Process (plan and ultraplan modes)

### Step 1: Understand Context
- List `Plans/` for related plans and read any that overlap with this feature
- List `Learnings/` then read any files that relate to the feature area (Blazor, TMDb, auth, deployment, etc.)
- Check CLAUDE.md for architectural constraints
- Check Memory for user preferences and project context

### Step 2: Research
Launch parallel Explore subagents to:
- Find affected files and dependencies
- Understand existing patterns in the codebase
- Research external APIs or libraries needed

### Step 3: Design Options
Present 2-3 approaches with:
- Pros/cons for each
- Effort estimate (small/medium/large)
- Impact on existing architecture

Ask the user which approach they prefer before proceeding.

### Step 4: Write the Plan or Hand Off to Ultraplan

**For `plan` mode** — create `Plans/<feature-name>.md`:

```markdown
# Feature Name - Implementation Plan

> **Status:** Draft | Active | Complete | On Hold
> **Phase:** [which roadmap phase this belongs to]

## Summary
[What, why, and chosen approach — 2-3 sentences]

## Phases

### Phase 1: [Name]
- [ ] Step 1
- [ ] Step 2

### Phase 2: [Name]
- [ ] Step 1

## Data Model Changes
[If applicable]

## API Changes
[New/modified endpoints]

## UI Changes
[New/modified pages/components]

## Testing
[How to verify]

## Rollback
[How to undo if needed]
```

**For `ultraplan` mode** — assemble a seed prompt from the research and hand off:

```
{Original task description} — chosen approach: {selected approach from Step 3}

CONTEXT:
- Design options considered: {summarize the 2-3 options and why this one was chosen}
- Affected files/areas: {from Step 2 research}
- Relevant learnings: {bullet list of title + how it applies}
- Related plans: {list}

PLAN FORMAT:
- Status front matter, Summary, numbered phases with - [ ] items
- Each phase ends with Testing/Verify step
- Data Model Changes, API Changes, UI Changes sections if applicable
- Rollback section
- Phases should be independently committable
```

1. Show the seed prompt for review
2. Save it to `Plans/<feature-name>-seed.md`
3. Output the invocation: `/ultraplan {seed prompt}`

### Step 5: Review
Present the plan summary to the user. **STOP and wait for approval before implementing.**

---

## Quick Mode

For simple tasks (single file, single concern), output an inline checklist and stop:

```markdown
## Quick Plan: {Task Name}

**Files touched:** {list}
**Dependencies:** {any}

- [ ] {task}
- [ ] {task}

**Verify:** {one-line check}
```

No file is saved. If the task turns out more complex, suggest switching to `plan` mode.

---

## Plan Sizing
- **Small** (1-2 phases): Bug fixes, minor features
- **Medium** (2-4 phases): New pages, API integrations
- **Large** (4-6 phases): Auth system, major refactors

## Rules
- Plans/ is local only (gitignored) — for working documents, not permanent docs
- One plan per feature, named descriptively
- Update plan checkboxes as you complete work
- Reference Learnings/ when they inform decisions
