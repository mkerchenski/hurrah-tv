# Start Here — Hurrah.tv

You're opening Hurrah.tv (a unified streaming watchlist: Blazor WASM client + .NET Minimal API + PostgreSQL, with AI curation). This page is a 5-minute orientation and a map — the real guides are linked below; this doc deliberately doesn't repeat them.

> Working in **Claude Code**? Great. Read the [diff-review rule](CONTRIBUTING.md#claude-code-workflow) before you accept any AI-written code, and lean on the [project skills](CONTRIBUTING.md#claude-skills) (`/xplan` → `/xsimplify` → `/xreview` → `/compound`).

## Which doc do I read?

| Doc | What it's for | Read it when |
|-----|---------------|--------------|
| [`README.md`](README.md) | What the product is, prerequisites, clone/build/setup commands, tech stack | First — to get it running |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | **The canonical "how we work."** First-day checklist, architecture primer, signed commits, git/PR workflow, browser testing, Claude skills, code style, finding work | Once end-to-end on day one; reference forever |
| [`CLAUDE.md`](CLAUDE.md) | Architecture, conventions, testing rules, the issue/label scheme, deploy details | When writing code or unsure of a convention |
| [`Learnings/`](Learnings/) | Hard-won, non-obvious gotchas captured per-incident (WASM, TMDb, Postgres, Blazor lifecycle, drag-reorder, etc.) | Before debugging something tricky — someone may have already paid for the answer |
| `Plans/` | Design docs / implementation plans (local-only, gitignored) | When `/xplan` saves one for a feature you're starting |

**Don't have time to read everything?** Do exactly this: get it running per README, then work through [CONTRIBUTING → Your first day](CONTRIBUTING.md#your-first-day) top to bottom. Everything else is reference.

## The two-minute mental model

Three projects, one rule that prevents most mistakes (full version: [architecture primer](CONTRIBUTING.md#architecture-primer)):

- **`HurrahTv.Api`** — the server. Holds every secret, owns the only DB connection, calls TMDb/Anthropic/Twilio.
- **`HurrahTv.Client`** — Blazor WASM, runs **in the browser**. Public by definition — **never** put a secret or a DB/external call here; route it through `ApiClient` → an API endpoint.
- **`HurrahTv.Shared`** — DTOs only, the shapes both sides agree on.

## How a change reaches users

```
branch off main → PR → Copilot review → @mkerchenski approves → squash-merge to main
   → staging auto-deploys (staging.hurrah.tv)        [GitHub Actions: main_hurrahtv.yml]
   → smoke-test staging
   → Mike runs /deploy → atomic slot swap to prod    [swap.yml]  (hurrah.tv)
```

You own everything up to the merge. Staging is automatic on merge; the production swap is Mike-only (`/deploy` — see the skills table). `main` is protected: signed commits, linear history (rebase/squash, never a merge commit), 1 approving review + code-owner, and PRs must be up to date with `main` before merging — the [PR workflow](CONTRIBUTING.md#pull-request-workflow) walks through all of it.

## Finding your first issue

Issues are the tracker (no board) — filter by label ([scheme](CONTRIBUTING.md#labels-and-how-to-find-work)). **`good first issue` / `difficulty:starter` items are curated for a new contributor** — start there, comment "I'd like to take this," and open a PR. When in doubt, ask in the issue rather than guessing.

---
*Setup problems or anything unclear: ping Mike — don't burn an hour stuck.*
