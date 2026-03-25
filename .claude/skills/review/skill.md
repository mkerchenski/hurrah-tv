---
description: Multi-agent code review for Hurrah.tv — runs parallel specialized reviewers on uncommitted or unpushed changes
user-invocable: true
---

# /review — Code Review

Run before committing or pushing. Launches parallel reviewers that catch different classes of issues.

## Usage
```
/review              # Review all uncommitted changes
/review --staged     # Only staged changes
/review --unpushed   # All commits not yet pushed
```

## Process

### Step 1: Gather Changes
Determine diff scope based on arguments:
- Default: `git diff` (unstaged + staged)
- `--staged`: `git diff --cached`
- `--unpushed`: `git diff origin/main...HEAD`

### Step 2: Format Check
Run `dotnet format --verify-no-changes --severity info` on the solution.
If issues found, ask user if you should auto-fix before review.

### Step 3: Launch Parallel Reviewers
Launch 4 specialized **Sonnet** subagents, each reviewing the same diff:

#### 1. CLAUDE.md Compliance
- Code style (explicit types, 4-space indent, no XML docs, lowercase comments)
- Architectural patterns (Minimal API endpoints, Dapper usage, shared DTOs)
- Naming conventions

#### 2. Blazor WASM & Client Architecture
- Component lifecycle issues (OnInitializedAsync vs OnParametersSetAsync)
- Memory leaks (event handlers, timers not disposed)
- Two-way binding pitfalls (the kind we hit in Phase 1)
- HttpClient usage patterns in WASM
- Render performance (unnecessary StateHasChanged, large component trees)

#### 3. API & Data Safety
- SQL injection via Dapper (parameterized queries)
- Missing input validation on endpoints
- CORS configuration
- TMDb API key exposure
- Proper error handling in API responses
- Auth bypass risks

#### 4. Bug & Logic Scan
- Null reference risks
- Async/await issues (fire-and-forget, deadlocks)
- Off-by-one errors
- Edge cases (empty lists, missing data, network failures)
- JSON serialization mismatches between API and Client

### Step 4: Score Issues
Each reviewer returns issues. Launch parallel **Haiku** subagents to score each issue 0-100:
- 80-100: Will cause bugs or security issues
- 50-79: Should fix before merging
- 0-49: Nitpick or style preference

### Step 5: Present Results
Show issues scoring 50+ in a table sorted by score:

| Score | Reviewer | File:Line | Issue | Suggestion |
|-------|----------|-----------|-------|------------|

### Step 6: Fix
Ask: "Would you like me to fix any of these issues?"
