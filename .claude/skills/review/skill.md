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

### Step 6: README Check
Verify that `README.md` accurately reflects the current state of the project:
- Tech stack matches what's actually used (database, AI, CSS build, auth)
- Feature descriptions match what's implemented
- Setup instructions are correct and complete
- Architecture diagram/table matches current project structure
If README is out of date, flag it as an issue and offer to update.

### Step 7: Fix
Ask: "Would you like me to fix any of these issues?"

### Step 8: File unaddressed findings as GitHub issues

After fixes (or if the user declines to fix), any finding scoring **50+** that is **not resolved in the current PR** should become a tracked follow-up issue — review notes should not disappear into PR comment history.

1. List findings that were NOT addressed in the fix step.
2. Ask: "File these as GitHub issues so they don't fall through the cracks?"
3. If yes, for each unaddressed finding create a GitHub issue via `gh issue create`:
   - **Title**: `[from review] <short finding summary>` (under 80 chars)
   - **Body** should include:
     - The reviewer's rationale (from the review output)
     - File and line references (`file.cs:123`)
     - Score and which reviewer category caught it (CLAUDE.md / Blazor / API / Bugs)
     - **Link back to the PR or commit range** this review covered — use `gh pr view --json url` if a PR exists, otherwise use the current branch name and diff range. Always include at least one link so the context is traceable.
     - A "Suggested approach" if the reviewer offered one
   - **Labels**: infer from the finding
     - Type: `type:bug` (score 80+ or bug-category), `type:refactor` (style/architecture), `type:enhancement` (nice-to-have)
     - Area: `area:api`, `area:client`, `area:infra`, `area:auth`, etc. — pick the best match based on file paths
     - Difficulty: `difficulty:starter` for small scope, `difficulty:intermediate` or `difficulty:advanced` for larger ones
   - **Use `gh issue create` with flags**: `--title`, `--body-file <tempfile>`, `--label <csv>`. Do not assign initially — let the team triage later.
4. After creating, report back a summary: `Filed N follow-up issues: #X, #Y, #Z` with their URLs so the user can add them to the PR description as `Related: #X #Y #Z`.

**Why**: review findings that get deferred without being tracked tend to reappear later as bugs or tech debt. Filing them as issues makes the deferral deliberate and scheduled, not forgotten. Also teaches new contributors that review notes are first-class work, not opinions.
