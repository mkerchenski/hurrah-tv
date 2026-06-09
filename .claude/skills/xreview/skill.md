---
name: xreview
description: Project-aware multi-agent code review for Hurrah.tv. Asks for a mode (quick / review), runs reviewers, fixes score-50+ findings inline by default, and always surfaces follow-up issue candidates (deferred / out-of-scope / adjacent improvements) for tracking.
argument-hint: [--staged|--unpushed]
---

# xreview — Hurrah.tv Augmented Multi-Agent Review

Wraps the system `review` skill with hurrah-tv-specific multi-agent infrastructure: 4 specialized reviewers (CLAUDE.md compliance, Blazor WASM lifecycle, API/Data safety, Bug scan), a dotnet format pass, and a README freshness check. After review, the default disposition is to fix every score-50+ finding inline, then always surface follow-up issue candidates for anything deferred, out-of-scope, or adjacent.

The fan-out-and-score core (the 4 specialized reviewers + per-finding scoring) runs as a deterministic **workflow** — `.claude/workflows/xreview.js`, invoked with `Workflow({name: "xreview", args: {...}})`. The workflow pipelines each dimension straight into scoring, so a finding starts scoring the moment its reviewer finishes. The system `review` skill runs concurrently in the main loop as a separate parallel voice; its findings are merged in afterward. The interactive parts — mode selection, dotnet format, inline fixes, README check, issue filing — stay in the main loop where they belong.

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

1. Gather the diff for the parsed scope (default: union as defined in Step 1-Review section 1) and externalize it (Step 1-Review section 3). Build the learnings index (section 1).
2. Empty-diff handling: if empty, report "No changes to review" and stop.
3. Run the xreview workflow with the **narrow** two-dimension subset — explicitly pass `dimensions: ["claudemd", "bugs"]`. Skip dotnet format, skip the system `review` call, skip the README check.
   ```
   Workflow({ name: "xreview", args: {
     diffPath, filesPath, claudeMdPath, learningsIndex,
     dimensions: ["claudemd", "bugs"]
   }})
   ```
4. The workflow returns `{ confirmed }` already scored and filtered to ≥ 50. Present per Step 1-Review section 6, with the smaller findings table.
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
- The CLAUDE.md path (pass the path to the workflow — agents read it; don't paste 200 lines into every prompt)
- **Learnings title index** — build a cheap one-line index, NOT a wholesale read. There are 80+ files in `Learnings/`; reading them all across every reviewer is the dominant cost. Capture the index string and pass it to the workflow so each agent reads only the 2–5 entries whose titles match its specialty:
  ```bash
  # filename + first-line title for every learning, one line each
  grep -rH -m1 '^# ' Learnings/ | sed 's/:# /  —  /'
  ```
  (Each line is `Learnings/<name>.md  —  <Title>`. An agent picks the relevant few by title and `Read`s only those.)
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

(`$TMPDIR` on macOS/Linux; `$env:TEMP` in PowerShell on Windows. Capture the absolute path of each file. Also capture the absolute path to `CLAUDE.md` and the learnings-index string from section 1 — these are the workflow's args.)

#### 4. Run the review workflow + system review concurrently

The 4 specialized reviewers and their per-finding scoring run as the **`xreview` workflow** (`.claude/workflows/xreview.js`). The four dimension prompts and the 0–100 scoring rubric live there canonically — don't duplicate them here. In a **single message**, do both of these so they run concurrently:

1. **Launch the workflow** with all four dimensions:
   ```
   Workflow({ name: "xreview", args: {
     diffPath:       "<abs path to hurrahtv-review-diff.txt>",
     filesPath:      "<abs path to hurrahtv-review-files.txt>",
     claudeMdPath:   "<abs path to CLAUDE.md>",
     learningsIndex: "<the grep index string from section 1>",
     dimensions:     ["claudemd", "blazor", "apidata", "bugs"],
     issueContext:   "<linked-issue acceptance criteria from section 1, or omit>"
   }})
   ```
   It pipelines each dimension straight into scoring (Sonnet reviewers → Haiku scorers) and returns `{ confirmed }` already filtered to score ≥ 50 and sorted descending. The workflow backgrounds; you're notified when it completes.

2. **Invoke `Skill(skill="review")`** as the separate parallel voice — the system review's generic PR-review pass. It runs inline in the main loop while the workflow churns.

**Always pass `dimensions` explicitly** — the workflow throws if it's missing rather than defaulting to "review everything," so a dropped arg surfaces loudly instead of silently widening scope. Tell the system review (and remember for the merge) to **ignore changes from dotnet format** — those are mechanical.

#### 5. Merge & score the system-review findings

The workflow's `confirmed` findings are already scored. The system `review` findings are not — score each against the **same rubric** (it lives in `xreview.js`; reproduced intent: 0 = false-positive/pre-existing, 50 = real but minor, 75 = important, 100 = confirmed-will-happen), keep only ≥ 50, and merge into one list. De-dupe where the system review and a specialized dimension flagged the same `file:line` — keep the higher score and note both reviewers. The merged, score-sorted list feeds section 6.

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
