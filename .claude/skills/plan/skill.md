---
description: Structured implementation planning — brainstorm, scope, and phase a feature before building
user-invocable: true
---

# /plan — Implementation Planning

## Philosophy
Spend 80% on planning, 20% on execution. Every non-trivial feature gets a plan before code.

## Process

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

Ask the user which approach they prefer.

### Step 4: Write the Plan
Create `Plans/<feature-name>.md` with:

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

### Step 5: Review
Present the plan summary to the user. **STOP and wait for approval before implementing.**

## Plan Sizing
- **Small** (1-2 phases): Bug fixes, minor features
- **Medium** (2-4 phases): New pages, API integrations
- **Large** (4-6 phases): Auth system, major refactors

## Rules
- Plans/ is local only (gitignored) — for working documents, not permanent docs
- One plan per feature, named descriptively
- Update plan checkboxes as you complete work
- Reference Learnings/ when they inform decisions
