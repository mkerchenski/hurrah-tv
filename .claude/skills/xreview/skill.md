---
name: xreview
description: Project-aware multi-agent code review for Hurrah.tv. Asks for a mode (quick / review), runs reviewers, fixes score-50+ findings inline by default, and always surfaces follow-up issue candidates (deferred / out-of-scope / adjacent improvements) for tracking.
argument-hint: [--staged|--unpushed]
---

# xreview — Hurrah.tv Augmented Multi-Agent Review

Wraps the system `review` skill with hurrah-tv-specific multi-agent infrastructure: 4 specialized reviewers (CLAUDE.md compliance, Blazor WASM lifecycle, API/Data safety, Bug scan), a dotnet format pass, and a README freshness check. After review, the default disposition is to fix every score-50+ finding inline, then always surface follow-up issue candidates for anything deferred, out-of-scope, or adjacent.

## Output rules (apply to every step below)

1. **NEVER post to GitHub automatically.** Do not post PR comments, reviews, or any content to GitHub without an explicit user instruction. Always present findings in the CLI first.
2. **Filter to score ≥ 50.** Issues scored below 50 are noise; do not report them.
3. **Do not add reviewers.** Never pass `--reviewer` flags.

## Workflow

### Step 0 — Mode selection

Ask:

> **Which review mode?**
>
> - **quick** — fast subset: CLAUDE.md compliance + Bug scan only. No dotnet format auto-fix. Inline fixes still default-on. For tight-loop checks during work-in-progress.
> - **review** — full local pipeline: system `review` + all 4 hurrah-tv reviewers + dotnet format auto-fix at info severity + README check + always-on follow-up issue surfacing.
>
> **When to pick `review`:** typical pre-commit / pre-push review.
> **When to pick `quick`:** sanity check during active work.

Parse `$ARGUMENTS` for scope (used in all modes):

| Argument       | Scope                                                                                     |
|----------------|-------------------------------------------------------------------------------------------|
| (empty)        | All work-in-progress: `git diff origin/main..HEAD` ∪ `git diff HEAD` (works on main too) |
| `--staged`     | Only staged changes (`git diff --cached`)                                                |
| `--unpushed`   | Only commits not yet pushed (`git diff origin/main..HEAD`)                               |

If `origin/main` doesn't exist locally, run `git fetch origin main` first. The default scope is deliberately branch-agnostic so it works whether the user is on a feature branch or committing direct to main.

Wait for mode response before proceeding.

---

### Step 1-Quick — fast subset

1. Gather the diff for the parsed scope (default: union as defined in Step 1-Review).
2. Empty-diff handling: if empty, report "No changes to review" and stop.
3. Launch only **Agent 1 (CLAUDE.md Compliance)** and **Agent 4 (Bug Scan)** as Sonnet subagents in parallel. Use the prompts from Step 1-Review section 4. Skip dotnet format. Skip the other 2 agents and the README check.
4. Score and present per Step 1-Review sections 5–6, but with a smaller findings table.
5. Apply Step 8 (default-fix) and Step 9 (follow-up surfacing) — same default-act behavior as the full review, just over a narrower finding set.

---

### Step 1-Review — full multi-agent pipeline

#### 1. Gather the diff

```bash
# default — covers feature-branch work AND committing-direct-to-main
git diff origin/main..HEAD     # committed-but-not-yet-on-origin
git diff HEAD                   # working tree (unstaged + staged)

# --staged
git diff --cached

# --unpushed
git diff origin/main..HEAD
```

The default is the union of the first two. If both empty → "No changes to review" and stop.

**Trivial-diff handling:** whitespace-only or csproj-version-bump-only diff → tell the user "diff is trivial; skip?" and wait.

Also gather:
- `git diff --name-only [range]` for changed files
- The CLAUDE.md content
- Any `Learnings/*.md` in scope of the diff
- **Linked GitHub issues** (only when scope is `--unpushed` or includes commits): parse commit messages for `#NNN` references and pull each issue's body so reviewer agents can compare the diff against acceptance criteria. Hurrah.Tv conventionally uses `closes #NN` / `fixes #NN` to auto-close on merge:
  ```bash
  git log <range> --format=%B | grep -oE '#[0-9]+' | sort -u
  # for each match:
  gh issue view <num> --repo mkerchenski/hurrah-tv --json title,body,labels,state
  ```
  Treat any issue's "Acceptance criteria" checklist as a hard checklist for the diff. If a commit says `closes #NN` but the diff doesn't appear to satisfy the criteria, that's a high-severity finding.

#### 2. Run dotnet format (auto-fix at info severity)

Run the three sub-commands in order, with explicit diagnostics on `style` to dodge the IDE0130 namespace-rename crash:

```bash
# 1. whitespace — no severity flag (sub-command doesn't accept it)
dotnet format whitespace HurrahTv.slnx --no-restore --verbosity minimal

# 2. style — IDE0130 excluded (its code-fix renames files which MSBuildWorkspace forbids)
dotnet format style HurrahTv.slnx --severity info --no-restore \
    --diagnostics IDE0008 IDE0011 IDE0017 IDE0021 IDE0028 IDE0029 IDE0034 \
                  IDE0037 IDE0040 IDE0046 IDE0053 IDE0058 IDE0059 IDE0090 \
                  IDE0270 IDE0290 IDE0300 IDE0301 IDE0303 IDE0305

# 3. analyzers — picks up CA-rule auto-fixes (regex source-gen, ToHexString, static, etc.)
dotnet format analyzers HurrahTv.slnx --severity info --no-restore --verbosity minimal
```

After:
- `git diff --stat` to see what changed
- `dotnet build HurrahTv.slnx --verbosity minimal` to confirm the auto-fixes didn't break anything
- Report: `dotnet format fixed N files: [bulleted list]`. Tell the review agents in step 4 to **ignore changes from format** — those are mechanical.

**If `dotnet format style` or `analyzers` crashes with `System.NotSupportedException: Changing document properties is not supported`:** narrow the `--diagnostics` list to exclude the offending rule (most often IDE0130).

**Info-level findings without applying:** `dotnet format HurrahTv.slnx --severity info --no-restore --verify-no-changes 2>&1`.

#### 3. Externalize the diff

```bash
git diff [range] > "$TMPDIR/hurrahtv-review-diff.txt"
git diff --name-only [range] > "$TMPDIR/hurrahtv-review-files.txt"
```

(`$TMPDIR` on macOS/Linux; `$env:TEMP` in PowerShell on Windows. Capture the absolute path of each file before launching subagents.)

#### 4. Launch in parallel: 4 agents + system review

In a single message, launch all 4 agents as **Sonnet** subagents (with `run_in_background: true`) plus invoke the system review skill. Each agent gets the diff path, the changed-files path, the CLAUDE.md content, and its specific prompt below.

Also call `Skill(skill="review")` as a parallel voice — the system review skill adds a generic PR-review pass alongside the hurrah-tv-specialized agents.

**Common prompt prefix for every agent:**
```
Review this diff for {your specialty}.
Diff path: {absolute path to hurrahtv-review-diff.txt}
Files path: {absolute path to hurrahtv-review-files.txt}
CLAUDE.md content: {paste here or read from file}
Read the full source of any changed files where the diff is insufficient.

Relevant learnings — before reviewing, glob and read Learnings/**/*.md and apply
any that bear on your specialty (Blazor lifecycle, WASM datetime, TMDb API, AI
curation, Postgres). If a learning conflicts with the diff, that's a finding worth flagging.

Report ONLY issues found in the diff, not pre-existing concerns. Format: list of
{file, line, issue, severity, explanation}.
```

##### Agent 1: CLAUDE.md Compliance

```
Review for violations of the project's CLAUDE.md conventions.

Key patterns to check:
- Code style: 4-space indentation, no XML doc comments, lowercase comments, Type over var
- No unnecessary abstractions, no over-engineering — three similar lines is better than premature abstraction
- Comments only where logic isn't self-explanatory; don't explain WHAT (well-named identifiers do that)
- Pre-computed counts: no `Count()` per render inside a loop
- Self-gating predicates over caller-supplied visibility flags
- Mutating endpoints return the updated entity
- Background work uses IServiceScopeFactory, not captured request-scoped services

Report ONLY violations clearly present in the diff. Do not flag pre-existing code.
```

##### Agent 2: Blazor WASM & Lifecycle

```
Review for Blazor WebAssembly issues.

Concerns:
- Component lifecycle: OnInitializedAsync vs OnParametersSetAsync usage
- Disposal: event handlers, timers, and singleton subscriptions unsubscribed in Dispose
- StateHasChanged: called correctly, not from background threads without InvokeAsync
- DI scope: Singleton consuming Scoped fails the DI validator (e.g. MediaFilterService → ApiClient)
- Two-way binding pitfalls
- HttpClient usage in WASM — auth handler attached, base address set
- Fire-and-forget — backgrounded tasks captured via _ = ... so they're not awaited synchronously
- Optimistic UI: mutate local state before API confirm; revert on failure
- Skeleton placeholders: any new async-loaded section should reserve its final shape

severity: high | medium | low.
```

##### Agent 3: API & Data Safety

```
Review for API and data safety issues.

Concerns:
- SQL injection: raw string concatenation in Dapper queries (use parameterized only)
- Postgres bigint → int32 mapping: COUNT(*), SUM() etc. need ::int cast or DTO must use long
- Missing input validation on endpoints (TmdbId > 0, MediaTypes.IsValid, enum bounds)
- Authentication: every user-facing endpoint has RequireAuthorization() or is intentionally AllowAnonymous
- Authorization policies: Admin policy is DB-backed (re-check per request), not just JWT-claim
- CORS configuration not weakened
- TMDb / Anthropic / Twilio API keys not exposed to the client
- Schema migrations: idempotent (IF NOT EXISTS), safe on existing data, backfill considered
- Background TmdbService work: fresh DI scope, no captured request services
- Error handling: bad-request validation returns 400 with explanation, not 500

Report ONLY confirmed or highly-likely issues. No theoretical concerns.
severity: critical | high | medium.
```

##### Agent 4: Bug & Logic Scan

```
Review for bugs, logic errors, and regressions.

Concerns:
- Null reference exceptions
- Off-by-one errors, incorrect loop bounds
- Async/await: missing await, fire-and-forget tasks that should be awaited, deadlocks
- Exception handling: swallowing exceptions, catching too broadly
- Logic inversions, negation errors
- Edge cases: empty collections, zero values, boundaries, network failures
- Regressions: does the change break existing behavior on Home / Queue / Details?
- Resource management: IDisposable not disposed, connections leaked
- JSON serialization mismatches between Shared DTO and how Dapper / API actually populate it
- DateTime UTC vs local mismatches (see Learnings/wasm-datetime-source-matters.md)

Ignore: version numbers, csproj metadata, cosmetic changes.
Report ONLY issues with real impact.
severity: critical | high | medium | low.
```

#### 5. Score issues

For each issue reported by any agent, launch a parallel **Haiku** subagent to score 0–100:

- **0**: false positive, doesn't hold up, or pre-existing
- **25**: might be real but unverified; stylistic and not in CLAUDE.md
- **50**: verified real but minor; nitpick relative to the change
- **75**: verified, important — directly impacts functionality or violates CLAUDE.md
- **100**: definitely real, confirmed with evidence, will happen in practice

Filter to score ≥ 50 (per Output Rules).

#### 6. Present results

If no issues score 50+:
```
### Code Review: No Issues Found

Reviewed N files. All 4 hurrah-tv agents + system review passed.
dotnet format: [clean / fixed N files]
README check: [up to date / flagged for update]
```

If issues exist, sort by score descending:

```
### Code Review Results

Reviewed N files.

| Score | Issue                                          | File:Line                  | Reviewer          | Severity |
|-------|------------------------------------------------|----------------------------|-------------------|----------|
| 85    | Missing await on UpdateProvidersAsync          | Endpoints/QueueEndpoints   | Bug & Logic       | High     |
| 72    | StateHasChanged called from background thread  | Pages/Home.razor:512       | Blazor            | Medium   |
```

#### 7. README Check

Verify `README.md` accurately reflects the current state:
- Tech stack matches what's actually used (PostgreSQL, Anthropic, Tailwind v4, phone-OTP auth)
- Feature descriptions match what's implemented (admin views, hero billboard, etc.)
- Setup instructions correct and complete
- Architecture diagram/table matches current project layout

If out of date, flag as an issue (count toward the score-50+ list) and offer to update.

#### 7a. Linked-issue comment (optional, opt-in only)

Skip entirely if no `#NNN` references were found in step 1's commit-message scan. Otherwise, after the README check and before the Fix prompt, offer to post a one-line comment on each linked issue summarizing the review state. **Default = no.** Per Output Rule "NEVER post to GitHub automatically," this requires explicit confirmation.

Use `AskUserQuestion` with three options:
- **Comment on all linked issues**
- **Pick which to comment on**
- **Skip** (Recommended default for routine reviews)

If picked, format per issue:
```bash
gh issue comment <num> --repo mkerchenski/hurrah-tv --body "/xreview ($(date +%Y-%m-%d), <range>): <one-line state>. Findings: <N issues at score ≥ 50, or 'clean'>."
```

If a linked issue's commit said `closes #NN` but the diff doesn't satisfy the issue's acceptance criteria (a reviewer agent flagged this), do NOT post automatically — surface to the user as a high-severity finding and let them decide whether to comment, fix the diff, or rewrite the issue body.

#### 8. Fix (default: act inline)

**Default behavior: fix every score-50+ finding inline** without asking. Apply the fix, then re-verify with `dotnet build`, `dotnet test`, and the format gate before continuing.

Only pause to ask the user when a specific finding is:
- **Ambiguous** — multiple reasonable approaches and the choice matters (e.g., refactor-vs-comment, broader API change vs. narrow fix).
- **Out-of-scope by the linked issue's own body** — e.g., the issue explicitly defers a sub-criterion to a follow-up. Surface as a follow-up suggestion in Step 9 instead of fixing.
- **Pre-existing** — flagged by a reviewer but not introduced by the diff. Surface as a follow-up suggestion in Step 9.
- **A false positive on re-trace** — state the rejection rationale in the user-facing summary and skip.

Do not auto-rerun the agents after fixing — the user re-invokes `/xreview` if they want a second pass.

After fixing, present a disposition table showing what was fixed inline, what was rejected (with rationale), and what's a follow-up candidate going into Step 9.

#### 9. Surface follow-up issue candidates (always run)

Always run this step after Step 8, even when every finding was fixed inline. Review work routinely surfaces *adjacent* improvements that aren't the immediate fix but are worth tracking. These get lost if not filed.

Build the follow-up candidate list from:
- **Unaddressed score-50+ findings** — anything skipped in Step 8 because it was ambiguous, out-of-scope, or pre-existing.
- **Issue-body-scoped follow-ups** — when a closed-by issue's body explicitly defers a sub-criterion to "a follow-up" (e.g., #112's body acknowledges match cancellation as out of scope). These are deferrals the issue author already approved.
- **Consistency gaps reviewers noticed** — same pattern present elsewhere in the codebase that wasn't touched by the diff (e.g., "GetWatchProvidersAsync has the same shape").
- **Pre-existing patterns** that reviewers flagged but couldn't act on inside the diff scope.
- **Adjacent low-severity nits below the score-50 filter** — only if a reviewer marked them as worth a future cleanup.

Present the candidate list with proposed title / body / labels for each, then ask which to file via `AskUserQuestion` with multi-select. Skip filing only for ones the user deselects.

For each filed issue (`gh issue create`):
- **Title**: `[from review] <short finding summary>` (under 80 chars)
- **Body**: reviewer's rationale, `file.cs:123` refs, score + reviewer category (CLAUDE.md / Blazor / API / Bugs), link to the PR/commit range (`gh pr view --json url` if a PR exists, else current branch + diff range), "Suggested approach" if known
- **Labels**: infer from the finding
  - Type: `type:bug` (score 80+ or bug-category), `type:refactor` (style/architecture), `type:enhancement` (nice-to-have)
  - Area: `area:api`, `area:client`, `area:infra`, `area:auth` — pick by file paths
  - Difficulty: `difficulty:intermediate` / `difficulty:advanced` (never `difficulty:starter` — that's reserved per user memory)
- Use `gh issue create --title --body-file <tempfile> --label <csv>`. Don't assign initially.

After filing: report `Filed N follow-up issues: #X, #Y, #Z` with URLs so the user can link them in the PR description as `Related: #X #Y #Z`.

**If there are no follow-up candidates** (rare but possible on small bug-fix PRs): report "no follow-up candidates surfaced" and skip the `AskUserQuestion`. Don't manufacture suggestions to fill space.

**Why**: deferred review findings that aren't tracked tend to reappear as bugs or tech debt. Filing them makes deferral deliberate, not forgotten.

---

## When to use

- Before committing significant changes (`review` mode)
- Before pushing
- After completing a feature, as a sanity check (`quick` mode is fine)
- When the user says "review my changes" or "check this code"
