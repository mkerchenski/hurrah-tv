# Contributing to Hurrah.tv

Welcome. This doc is the canonical "how we work" guide. Read it once end-to-end on your first day; reference it whenever you're unsure about the workflow.

If you want to run the project locally, start with [`README.md`](README.md) for prereqs and setup commands. This doc picks up where that leaves off.

> **A note on scope:** Hurrah.tv is a small passion project, not an open-source community. The "All rights reserved" license is intentional. If you're reading this as an external visitor, feel free to look around — but PRs from outside the team won't be merged.

---

<a id="your-first-day"></a>
## Your first day

Run through this top-to-bottom. Most steps link to deeper sections below. If any step fails, ping Mike — don't spend an hour stuck.

- [ ] Clone the repo and install prerequisites: .NET 10 SDK, Node 22+, PostgreSQL 17, VS Code. See [First-time setup](#first-time-setup).
- [ ] `dotnet dev-certs https --trust` — accept the OS prompt so `https://localhost` works.
- [ ] Get `appsettings.Development.json` from Mike. It holds the TMDb, Anthropic, Twilio, and DB keys. Shared out-of-band (not in git). Drop it into `HurrahTv.Api/`.
- [ ] Configure [signed commits](#signed-commits). Branch protection rejects unsigned commits — if you skip this, your first push will bounce.
- [ ] Open `Hurrah.tv.code-workspace` in VS Code (File → Open Workspace from File). Install recommended extensions when prompted.
- [ ] Run `Cmd/Ctrl+Shift+P → Tasks: Run Task → Watch All (API + Client)`. Open https://localhost:7267 in Chrome.
- [ ] Sign in with your phone (OTP flow), add a show to your watchlist, confirm it renders. That proves the whole stack — client, API, Postgres, Twilio — is working.
- [ ] Pick a [`good first issue`](https://github.com/mkerchenski/hurrah-tv/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22), comment "I'd like to take this", open a [PR](#pull-request-workflow).

---

## Table of contents

- [Your first day](#your-first-day)
- [Architecture primer — API vs Client vs Shared](#architecture-primer)
- [First-time setup](#first-time-setup)
- [Opening the project in VS Code](#opening-the-project-in-vs-code)
- [When to rebuild Tailwind CSS](#when-to-rebuild-tailwind-css)
- [Git workflow](#git-workflow)
- [Claude Code workflow — the diff-review rule](#claude-code-workflow)
- [Browser testing — Chrome DevTools + mobile mode](#browser-testing)
- [Pull request workflow](#pull-request-workflow)
- [Claude skills on this repo](#claude-skills)
- [Code style](#code-style)
- [Labels and how to find work](#labels-and-how-to-find-work)

---

<a id="architecture-primer"></a>
## Architecture primer — API vs Client vs Shared

The solution has three projects. Understanding the split is the single most important thing to learn before writing code.

### `HurrahTv.Api` — the backend

- Runs on a server (Azure App Service in production, your machine in dev)
- Holds **every secret**: TMDb key, Anthropic key, Twilio credentials, PostgreSQL connection string, JWT signing key
- Only thing that talks to PostgreSQL
- Only thing that calls external APIs (TMDb, Anthropic, Twilio)
- Exposes endpoints in `HurrahTv.Api/Endpoints/*.cs` using .NET Minimal API style

### `HurrahTv.Client` — the frontend

- Blazor WebAssembly — compiles to WASM and runs **in the user's browser**
- Everything in this project is **public by definition**. Open DevTools, you can read the whole bundle.
- **Cannot hold a secret.** If you put an API key here, it's a security incident.
- **Cannot talk to the database.** Browsers don't have network access to Postgres.
- Anything that needs a secret or a DB query goes through `ApiClient.cs` (typed HttpClient wrapper) → HTTP → API

### `HurrahTv.Shared` — the contract

- DTOs only — the types both sides agree on
- If the API returns it or the Client sends it, its shape lives here
- No behavior, no business logic — just shapes

### Request flow example — "Add to Queue"

```
User clicks "Add to Queue" button
  → QuickActions.razor (Client)
    → ApiClient.AddToQueueAsync(dto)     // Client-side
      → HTTP POST /api/queue              // network boundary
        → QueueEndpoints.cs (API)
          → DbService.AddQueueItemAsync   // Dapper → Postgres
            → returns DTO
          → Client receives DTO
            → component re-renders
```

### Rule of thumb

If you feel tempted to put an API key, a SQL query, or server-only logic in `HurrahTv.Client`, stop — it belongs in `HurrahTv.Api` with an endpoint that Client calls. Ask Claude or Mike if unsure.

---

<a id="first-time-setup"></a>
## First-time setup

Run through [`README.md`](README.md) first for prerequisites and the basic clone/build. This section adds the delta:

1. `dotnet dev-certs https --trust` — accept the prompt so HTTPS localhost works
2. **Secrets**: `appsettings.Development.json` is shared out-of-band (Mike emails it). If the file exists in `HurrahTv.Api/` and the app boots, you're set. If keys rotate or something breaks, ping Mike for a fresh copy — do NOT commit this file (it's gitignored)
3. PostgreSQL 17 running locally:
   - **Mac:** `brew install postgresql@17 && brew services start postgresql@17`
   - **PC:** `winget install PostgreSQL.PostgreSQL` (or the installer)
4. Both API and Client are configured to run on these ports:
   - API: `https://localhost:7201`
   - Client: `https://localhost:7267` ← open this in Chrome

<a id="signed-commits"></a>
### Signed commits

`main` is protected by a ruleset that rejects unsigned commits. SSH signing with your existing GitHub SSH key is the easiest path:

```bash
git config --global gpg.format ssh
git config --global user.signingkey ~/.ssh/id_ed25519.pub   # or whatever key you use
git config --global commit.gpgsign true
```

Then in GitHub: **Settings → SSH and GPG keys → New SSH key**, set **Key type: Signing Key**, and paste the same public key you use for auth. You need *both* an Authentication key entry and a Signing key entry, even when the key material is identical.

Verify on the next commit:

```bash
git commit --allow-empty -m "signing test"
git log -1 --show-signature
# expected: "Good 'git' signature" line
```

If you see `gpg.ssh.allowedSignersFile needs to be configured`, run:

```bash
echo "$(git config user.email) namespaces=\"git\" $(cat ~/.ssh/id_ed25519.pub)" > ~/.ssh/allowed_signers
git config --global gpg.ssh.allowedSignersFile ~/.ssh/allowed_signers
```

That creates an allowed-signers file with your own email + key, which is what local verification needs. (GitHub does its own verification against the uploaded signing key — the allowed-signers file only affects local `git log --show-signature`.)

---

<a id="opening-the-project-in-vs-code"></a>
## Opening the project in VS Code

**Always open via the workspace file, not "Open Folder" or the `.sln`.** The workspace file configures extensions, tasks, formatting, and multi-root project boundaries.

### How to open

1. In VS Code: **File → Open Workspace from File…**
2. Navigate to the repo root and select `Hurrah.tv.code-workspace`
3. When VS Code prompts "This workspace has extension recommendations," click **Install All**

Alternative: in Finder (Mac) or Explorer (Windows), double-click `Hurrah.tv.code-workspace` — it opens in VS Code directly.

### How you know it worked

- The sidebar Explorer shows multiple root folders (`HurrahTv.Api`, `HurrahTv.Client`, `HurrahTv.Shared`, `Root`)
- The window title reads "Hurrah.tv (Workspace)"
- `Cmd/Ctrl+Shift+P → Tasks: Run Task` shows "Watch API", "Watch Client", "Watch All", "Build CSS"

### Running the app

Use `Cmd/Ctrl+Shift+P → Tasks: Run Task → Watch All (API + Client)`. This starts both projects with hot-reload in dedicated terminal panels. Navigate to `https://localhost:7267`.

Want to run them separately? Use the individual "Watch API" and "Watch Client" tasks instead.

### IDE alternatives

- **Visual Studio 2022+** (PC): ignores the workspace file; opens `HurrahTv.sln` as a multi-project solution. Use VS's built-in multi-project startup.
- **JetBrains Rider**: open `HurrahTv.sln`. Works fine, but you lose the VS Code task presets.

---

<a id="when-to-rebuild-tailwind-css"></a>
## When to rebuild Tailwind CSS

Tailwind is a CLI build — it scans `.razor` and `.html` for classes and writes a final CSS file. If you forget to rebuild, your new classes silently do nothing.

### You MUST rebuild after:

- Adding or changing `class="..."` attributes in any `.razor` file
- Adding a new icon (Heroicons are pulled by name at build time)
- Editing `HurrahTv.Client/tailwind.config.js`
- Editing `HurrahTv.Client/app.css` (the `@apply` / `@layer` source)
- Pulling changes from `main` that touched any of the above

### How to rebuild

**Canonical command** — one-shot rebuild of both CSS and icons:

```bash
cd HurrahTv.Client && npm run build:css
```

Or via VS Code: `Cmd/Ctrl+Shift+P → Tasks: Run Task → Build CSS (rebuild + icons)`.

**Rapid iteration mode** — if you're only tweaking class names and not touching icons, you can use the watch task:

- VS Code task: `Build CSS (watch, Tailwind only — no icons)`
- Equivalent: `npm run dev:css` from `HurrahTv.Client/`

⚠️ Watch mode **does not regenerate icons**. If you add or change an icon, stop the watch task and run `npm run build:css` manually.

### Gotcha

If your UI change "isn't showing up" — nine times out of ten it's because CSS hasn't rebuilt. Check that you ran `npm run build:css` (or that the watch task is running) before debugging further. If you added a new icon and watch mode is running, remember icons aren't auto-regenerated — one-shot rebuild required.

---

<a id="git-workflow"></a>
## Git workflow

### Before any new work

```bash
git checkout main
git pull
```

**Always.** Branches are cheap; never code on `main`.

### Branch naming

- `feature/<short-description>` — new capability
- `fix/<short-description>` — bug fix
- `chore/<short-description>` — infra, tooling, docs
- `refactor/<short-description>` — internal change

Keep the description short and kebab-case. Examples: `feature/export-my-data`, `fix/details-queue-fetch`, `chore/add-dependabot`.

### Commits

- Small, frequent commits beat one giant commit
- Write your own messages — don't paste what Claude suggests
- First line: under 72 chars, imperative mood ("Add X", not "Added X" or "Adds X")
- Body (optional): explain *why*, not *what* (the diff shows what)

### Push

```bash
git push -u origin <branch-name>
```

Then open a PR (see [Pull request workflow](#pull-request-workflow)).

---

<a id="claude-code-workflow"></a>
## Claude Code workflow — the diff-review rule

Claude Code is a force multiplier, but it will confidently write code you don't understand. The single habit that separates AI-assisted engineering from AI slop:

> **After Claude edits files, open VS Code's Source Control panel (`Cmd/Ctrl+Shift+G`), click each changed file, and read the diff. If you can't explain a line, ask Claude to explain it or revert it.**

### Concretely

1. Let Claude make the change
2. Open Source Control panel (`Cmd/Ctrl+Shift+G`)
3. Click each file in the "Changes" list — VS Code shows a side-by-side diff
4. Read every hunk. For each line you don't understand:
   - Ask Claude: "Explain line N of file X — why is this here?"
   - Or revert it if it's clearly wrong
5. Use the `+` icon on individual diff chunks to stage only what you've read and understood
6. **Do not `git add -A`.** Staging per-hunk is `git add -p` with training wheels. Get used to it.
7. Write your own commit message based on what you understood

### Why this matters

Employers are already learning to spot PRs where the author can't explain the code. Habits you form in the first few months compound. Being the person who *understands* their AI-assisted code is a durable career advantage.

---

<a id="browser-testing"></a>
## Browser testing — Chrome DevTools + mobile mode

Hurrah.tv is mobile-first — half the users are on phones. Test both.

### Open DevTools

- Mac: `Cmd+Option+I`
- PC: `F12`

### Tabs you care about

- **Console** — watch for red errors while you click through your change. Blazor WASM exceptions surface here with full stack traces. Fix any errors your change introduces.
- **Network** — verify API calls fire as expected, inspect request/response payloads, no 404s or 500s
- **Application** — localStorage (JWT token lives here as `hurrah_jwt`), service worker (for PWA)
- **Elements** — inspect rendered HTML/CSS; useful for Tailwind class debugging

### Mobile mode

- Toggle device toolbar: `Cmd+Shift+M` (Mac) / `Ctrl+Shift+M` (PC)
- Pick **iPhone 14 Pro** or similar
- Test every UI change at both desktop (≥1024px) AND mobile (<640px) widths before opening a PR
- Test portrait AND landscape in mobile mode for content that involves video or episode browsing
- Test touch interactions: tap with mouse, long-press (hold), tab through form fields

### Required for UI PRs

Include BOTH desktop and mobile screenshots in your pull request. The PR template has a table for them.

---

<a id="pull-request-workflow"></a>
## Pull request workflow

1. Push your branch: `git push -u origin <branch>`
2. Open the PR against `main`. The PR template will auto-populate — fill it in.
3. **Request review from GitHub Copilot** (Reviewers panel → Copilot)
4. Wait for Copilot's comments; address them (or explain why you're declining)
5. Once Copilot pass is clean, **assign `@mkerchenski`** as reviewer
6. **Do not merge.** Mike reviews, may request changes, and squash-merges from `main`
7. Branch protection enforces this — you physically cannot merge your own PR, so there's no way to fat-finger

### If Mike requests changes

- Push more commits to the same branch — the PR updates automatically
- Do not rebase or force-push unless asked (keeps the review-history readable)
- Reply to Mike's comments with either "fixed in abc1234" or a question if something's unclear

### If your PR falls behind `main`

Branch protection requires PRs to be up-to-date with `main` before merging (strict status checks). If `main` advances after your CI passed, GitHub shows an **Update branch** button on the PR. Click it — GitHub merges `main` into your branch, CI re-runs, and once it's green Mike can merge.

From the command line:

```bash
git fetch origin
git rebase origin/main
git push --force-with-lease
```

Prefer rebase over merge — the ruleset requires linear history, so a merge-commit update will eventually get rejected anyway. `--force-with-lease` is the safe variant of `--force`: it refuses to clobber the remote if someone else pushed to your branch in the meantime.

---

<a id="claude-skills"></a>
## Claude skills on this repo

Claude Code skills are slash-commands that encode specific workflows. Use these:

| Skill | When to use |
| --- | --- |
| `/plan` | Before any non-trivial feature. Saves you from writing code you throw away. |
| `/review` | Before opening a PR. Runs parallel reviewers on your changes and files follow-up issues for anything you don't fix. |
| `/compound` | After fixing a tricky bug. Captures the learning into `Learnings/` so the next person (maybe you) finds it. |
| `/design` | Any UI work. Applies the Netflix-inspired dark theme consistently. |
| `/security-review` | If you ever touch auth, JWT, OTP, or secrets handling. |
| `/deploy` | Production deploys. Ryan — don't use this yet. Ask Mike. |

Other skills (`/csharp`, `/dotnet-blazor`, `/workspace`, etc.) are available — browse with Tab completion or `/help`.

---

<a id="code-style"></a>
## Code style

Detailed style rules live in [`CLAUDE.md`](CLAUDE.md). Highlights:

- 4-space indentation (enforced by `.editorconfig`)
- Nullable reference types enabled — if the compiler warns, fix it; don't suppress
- Prefer `Type variableName` over `var` when the type isn't complex — helps readers
- **No XML doc comments.** Use `//` comments only when the *why* isn't obvious from the code
- Comments start lowercase
- Pre-compute per-status counts with `GroupBy().ToDictionary()`; never `Count()` per tab inside a render loop
- Self-gating components: if a UI control's visibility can be derived from the item's own data, encode that in the component, not as a `showX` prop from every caller

---

<a id="labels-and-how-to-find-work"></a>
## Labels and how to find work

All work lives in GitHub Issues. Labels help you filter.

### Types
- `type:bug`, `type:feature`, `type:enhancement`, `type:refactor`, `type:chore`, `type:docs`

### Areas
- `area:api`, `area:client`, `area:infra`, `area:auth`, `area:tmdb`, `area:ai-curation`, `area:design`, `area:docs`

### Difficulty
- `difficulty:starter` — small scope, clear definition of done, good for first contribution
- `difficulty:intermediate` — touches multiple files or requires design thinking
- `difficulty:advanced` — architectural change or cross-cutting concerns

### Phase
- `phase:now` — actively worked or ready to pick up
- `phase:next` — planned but not started
- `phase:future` — long-term; revisit later

### Useful filtered views

- [Good first issues](https://github.com/mkerchenski/hurrah-tv/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
- [Starter difficulty](https://github.com/mkerchenski/hurrah-tv/issues?q=is%3Aissue+is%3Aopen+label%3A%22difficulty%3Astarter%22)
- [Client-only](https://github.com/mkerchenski/hurrah-tv/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Aclient)
- [API-only](https://github.com/mkerchenski/hurrah-tv/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%3Aapi)
- [All open PRs](https://github.com/mkerchenski/hurrah-tv/pulls)
- [Recent commits](https://github.com/mkerchenski/hurrah-tv/commits/main)

### Before starting an issue

1. Read the whole issue body + any linked files or learnings
2. Comment "I'd like to take this" so we don't duplicate work
3. If anything's unclear, ask in the issue comments rather than guessing
