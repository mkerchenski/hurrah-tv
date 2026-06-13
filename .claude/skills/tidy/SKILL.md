---
description: Post-merge cleanup — switch to the default branch, pull, and delete the merged feature branch (with a safety check that the PR actually merged)
argument-hint: "[branch] (defaults to the current branch)"
user-invocable: true
---

# /tidy — Post-Merge Branch Cleanup

**Target branch:** $ARGUMENTS (empty = the branch you're currently on)

Run after a PR has been **squash-merged** on GitHub. Squash merges leave the local
branch looking "unmerged" to git (no shared commit), so a plain `git branch -d`
refuses it — this skill confirms the merge happened *on GitHub* first, then force-deletes
safely.

Repo-agnostic: every `gh` call infers the repo from the current directory, and the
default branch is detected (not assumed to be `main`).

## Steps

### 1. Resolve target + default branch

```bash
TARGET="${ARGUMENTS:-$(git branch --show-current)}"
DEFAULT="$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's@^origin/@@')"
DEFAULT="${DEFAULT:-main}"
echo "target: $TARGET   default: $DEFAULT"
```

- If `$TARGET` equals `$DEFAULT` → nothing to delete. Just run step 3 (sync) and stop.
- If the working tree is dirty (`git status --porcelain` non-empty) → **stop and tell the user.**
  Don't switch branches over uncommitted work.

### 2. Safety check — did the PR actually merge?

Squash-merge means git can't tell locally, so ask GitHub (repo inferred from cwd):

```bash
gh pr list --head "$TARGET" --state merged --json number,title,mergedAt \
  --jq '.[0] | "merged: #\(.number) \(.title) @ \(.mergedAt)"'
```

- **A merged PR is returned** → safe to delete. Proceed.
- **Empty result** → the branch's PR is NOT merged (still open, closed-unmerged, or never had a PR).
  **Stop and warn the user** — show `git log $DEFAULT..$TARGET --oneline` (commits that would be lost)
  and ask for explicit confirmation before deleting. Never silently `-D` an unmerged branch.

### 3. Sync the default branch

```bash
git checkout "$DEFAULT"
git pull --ff-only origin "$DEFAULT"
```

If `--ff-only` fails (local default diverged), stop and surface it — don't force anything.

### 4. Delete the local branch + prune remotes

```bash
git branch -D "$TARGET"          # -D because squash-merge leaves it "unmerged" locally
git remote prune origin          # drop the now-deleted origin/$TARGET ref (and any other stale ones)
```

### 5. Report

One line: what was pulled into the default branch (`git log --oneline -1`) and which branch was deleted.
If `git remote prune` removed extra stale refs, mention them — it's a useful side-effect, not an error.

## Guidelines

- **The GitHub merge check (step 2) is the load-bearing safety.** It's what makes `-D` safe
  after a squash merge. Skipping it risks deleting genuinely unmerged work.
- This skill never merges, pushes, or touches the remote branch beyond pruning the dead ref —
  GitHub's "delete branch on merge" (or the squash-merge UI) already removed `origin/$TARGET`.
- Branch/PR structure is the user's to own — this only cleans up *after* a merge the user already performed.
