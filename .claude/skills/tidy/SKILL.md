---
description: Post-merge cleanup — switch to the default branch, pull, and delete the merged feature branch (with a safety check that the PR actually merged). Run on the default branch itself, it commits and pushes any dirty work instead.
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

- If `$TARGET` equals `$DEFAULT` → nothing to delete. **Run step 3a (sync + commit + push) and stop.**
- Otherwise (`$TARGET` is a feature branch) and the working tree is dirty
  (`git status --porcelain` non-empty) → **stop and tell the user.**
  Don't switch branches over uncommitted work.

### 3a. On the default branch: pull, commit & push

Only when `$TARGET` equals `$DEFAULT`. This replaces step 3 — don't also run step 3.

**First, pull any unpulled commits.** Always fetch and check whether the local default
is behind the remote, even if the tree is dirty:

```bash
git fetch origin "$DEFAULT"
git status --porcelain                                   # what's about to be committed (may be empty)
git rev-list --left-right --count "origin/$DEFAULT...HEAD"   # "<behind>  <ahead>"
```

- **Clean tree** → `git pull --ff-only origin "$DEFAULT"` to absorb any unpulled commits.
  Then if there's nothing to commit, report "nothing to commit, already synced" and stop.
- **Dirty tree** → commit first (see below), then reconcile any unpulled commits with a
  rebase so your commit lands cleanly on top:
  ```bash
  git add -A && git commit            # message per the rules below
  git pull --rebase origin "$DEFAULT" # no-op if not behind; replays your commit if behind
  git push origin "$DEFAULT"
  ```

Commit message rules:
- Write a concise message summarizing the actual changes — read the diff first;
  don't use a generic placeholder. End it with the standard `Co-Authored-By` trailer.

Safety:
- Never force-push the default branch. If the rebase hits a conflict, **stop** and surface it
  — don't auto-resolve. If the push is still rejected after the rebase (remote moved again),
  re-run the `pull --rebase` then retry the push once; if it still fails, stop and report.

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
When run on the default branch, instead report what was committed & pushed (or "nothing to commit" if the tree was clean).

## Guidelines

- **The GitHub merge check (step 2) is the load-bearing safety.** It's what makes `-D` safe
  after a squash merge. Skipping it risks deleting genuinely unmerged work.
- On a **feature branch**, this skill never merges or pushes — it only cleans up after a merge
  the user already performed (GitHub's "delete branch on merge" already removed `origin/$TARGET`,
  so it just prunes the dead ref). On the **default branch**, it commits & pushes dirty work (step 3a).
- Branch/PR structure is the user's to own — this only cleans up *after* a merge the user already performed.
