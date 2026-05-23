# User-Controllable Ordering Is Meaningful Only Where Algorithmic Signals Tie

> **Area:** UI | Architecture | Data
> **Date:** 2026-05-07

## Context

Issue #39 wanted drag-reorder on the queue. The `QueueItems` table has a `Position INT` column already used as a sort tiebreaker. The naive read is "Position drives order — drag changes Position — drag works on every tab." Reality is subtler: `GetQueueAsync` sorts by **four** keys in this order:

1. `Status` (Watching → WantToWatch → Finished → NotForMe)
2. `LatestEpisodeDate >= NOW() - INTERVAL '7 days'` (fresh-episode bucket)
3. `LatestEpisodeDate DESC`
4. `Position`

So `Position` only decides ordering when the first three keys all tie. On the Watching tab, recency dominates and a user's drag would either look invisible (snap back) or require us to suppress the recency sort — which is itself a desirable feature. On the All tab, Status dominates and dragging across statuses snaps back. Position is the *primary* sort signal in practice only on **WantToWatch**, where items typically share a null `LatestEpisodeDate` (user hasn't started watching) and collapse into one big tier.

## Learning

**Before exposing a "let the user customize sort order" affordance, find the algorithmic sort tiers and ask: at which tier does the user's input actually decide ordering?** That's the only tier where the affordance has a visible effect; expose it there and hide it elsewhere. Don't fight the algorithmic signals — they exist for a reason (recency, freshness, type-priority) and the user values them too.

Concretely, when adding a user-controlled ranking feature:

1. Read every `ORDER BY` against the relevant table — most apps have several, in different endpoints.
2. Map where the user-controlled column appears in each: primary key, secondary, tiebreaker, ignored.
3. Audit every surface that renders the same data — list page, home rows, summary cards, admin views — and identify whether each uses the same query or a different sort altogether.
4. Scope the affordance (drag handle, "set priority" button, etc.) to the surfaces where the user-controlled key dominates. Hide it elsewhere (don't disable — *absent* is better than disabled, because disabled invites "why doesn't this work?").
5. Decide explicitly what cross-surface semantics mean: the user changing rank in surface A may or may not propagate visibly to surface B. Document the answer in the plan.

This is not just about drag-reorder. It applies to:

- **Pinning / "show me first"** — pinning is meaningful only where the pin column outranks the algorithmic sort.
- **Custom watchlists / saved orderings** — only useful if the rendering surface uses the saved ordering, not its own algorithm.
- **AI-curated ordering vs. user override** — the AI score is one tier; user preference is another; pick which wins.

## Anti-pattern: "let the user reorder everywhere, snap back where it doesn't fit"

Tempting (one global Position column, drag handle on every list) but produces the worst UX: the user drags, the row snaps somewhere unexpected, they conclude the feature is broken. Better to *not show* the drag affordance than to show it where it doesn't bite.

## Anti-pattern: per-status / per-context Position columns to make drag "work everywhere"

Adds schema complexity (one Position column per context, renumbering on transitions) just to give drag the *appearance* of working — but it still fights the algorithmic signal in those contexts. If recency-of-new-episodes is the right Watching-tab sort, the user's drag in that tab is *meaningless* even with a separate position column. The right answer is "drag is the wrong feature for this surface," not "let's re-engineer the data model so drag can pretend to work here."

## Concrete file pointers from #39

- `HurrahTv.Api/Services/DbService.cs` — `GetQueueAsync` (the 4-key ORDER BY)
- `HurrahTv.Client/Pages/Queue.razor` — drag handle gated on `_activeTab == WantToWatchTab`
- `HurrahTv.Client/Pages/Home.razor` — uses its own client-side sort modes (`date` / `sentiment` / `queue`) — Position is at most a tertiary key, so drag-reorder propagates there only in `queue` mode and that's coherent
- `Plans/issue-39-queue-drag-drop-reorder.md` — full audit of every queue-rendering surface

## Generalization

The deeper rule: *a user-controlled signal is only as visible as the lowest sort tier it occupies.* If your column is tier-N and tiers 1..N-1 have non-trivial discriminators on the rendered subset, the user can't see their input. Find tiers where the higher discriminators tie on the visible subset, and surface the control only there.

## Postscript (2026-05-23): the assumption that broke

This learning's recommendation was: *gate drag-reorder to Want-to-Watch only*, because Want-to-Watch items "typically share a null `LatestEpisodeDate` (user hasn't started watching) and collapse into one big tier."

That assumption was wrong, and the failure surfaced as **#101**: drag-reorder visually worked but didn't persist on refresh.

The reality: `LatestEpisodeDate` gets populated by `TmdbService`'s background refresh path for *every* queue item that has a TV show with aired episodes — Status doesn't matter. A user can sit on a Want-to-Watch list of 20 shows where 18 have a populated `LatestEpisodeDate` (because TMDb knows their air date) and only 2 are null. The `LatestEpisodeDate DESC` tier dominates Position, and the user's drag is invisible to the GET.

The learning's audit step is still correct ("read every ORDER BY against the table"). What it missed was the second-order check: **for each tier, is the *value* of that column actually NULL/uniform on the rendered subset, or is it populated by a side-effect the audit didn't trace?** Schema nullability says "could be null"; the runtime data shape says "is populated by TMDb refresh."

The structural fix (commit `292bf82` for #101): branch the SQL `ORDER BY` on `Status` so Want-to-Watch shortcuts past the recency tiers and ranks by `Position` only. Other statuses keep the recency-aware sort that surfaces new episodes:

```sql
ORDER BY
    CASE Status WHEN 1 THEN 0 WHEN 0 THEN 1 WHEN 2 THEN 2 WHEN 4 THEN 3 ELSE 4 END,
    CASE WHEN Status = 0 THEN Position END ASC NULLS LAST,
    CASE WHEN Status != 0 AND LatestEpisodeDate >= NOW() - INTERVAL '7 days'
          AND LatestEpisodeDate <= NOW() THEN 0 ELSE 1 END,
    CASE WHEN Status != 0 THEN LatestEpisodeDate END DESC NULLS LAST,
    Position
```

The `CASE WHEN Status = 0 THEN ... END` + `NULLS LAST` pattern partitions per status: Want-to-Watch sorts by Position, other statuses fall through to the recency keys. Both halves share the *same* `ORDER BY` so one query continues to serve every queue tab.

**Meta-learning:** when an audit's mitigation depends on a runtime data-shape assumption, validate the assumption against actual rows in the database, not against the schema's nullable column declarations. The schema says "could be null"; the prod data may have populated 95% of those nulls via a side-effect path the audit didn't trace.
