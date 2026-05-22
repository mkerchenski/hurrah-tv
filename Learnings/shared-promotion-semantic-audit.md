# Promoting Rules to Shared: Audit Semantic Consumers Across Languages

> **Area:** Architecture | Data
> **Date:** 2026-05-22
> **Resolves:** mkerchenski/hurrah-tv#86, mkerchenski/hurrah-tv#91

## Context

PR #93 closed two related issues in the same diff:

- **#86** added `<= DateTime.UtcNow` upper bounds to `QueueItem.HasNewEpisode` and `HasEpisodeThisMonth`. The acceptance criterion read: *"Audit consumers of `HasNewEpisode` and `HasEpisodeThisMonth` to confirm none rely on future-date inclusion."*
- **#91** promoted the `Watching → WantToWatch → Finished → NotForMe` ordering rule from `BadgeHelpers.AllStatuses` (Client) to `QueueStatusOrdering.DisplayOrder` (Shared), with `SortPriority` derived from it.

The "audit consumers" criterion for #86 was satisfied — I checked every C# caller of the predicates (`Home.razor`, `Queue.razor`, `PosterCard`) and confirmed none of them benefited from future-date inclusion. The PR shipped.

The multi-agent review (`/xreview`) on the PR independently flagged two further problems that the original audit had missed:

1. **`DbService.GetQueueAsync` SQL** had a freshness-classifier `CASE WHEN LatestEpisodeDate >= NOW() - INTERVAL '7 days' THEN 0 ELSE 1 END` — the **exact same one-sided window** as the C# predicate I just fixed, implemented directly in SQL against the underlying column.
2. **The same SQL had a status `CASE`** that mirrored `QueueStatusOrdering.SortPriority` but lacked an `ELSE` clause — so when (not if) a new `QueueStatus` enum value gets added, SQL falls through to NULL while C# returns `DisplayOrder.Count`. The two sides diverge on the next enum addition.

Three of four reviewer agents flagged at least one of these. Copilot's automated review caught the missing `ELSE` independently.

## Learning

**When promoting a rule to Shared — or fixing a bug in a predicate — the "audit consumers" step must cover semantic consumers across languages, not just callers in the same language.**

A SQL `CASE` expression that classifies the same input by the same window is *the same rule* as the C# predicate, even though no static analyzer or call-site grep will link them. The C# compiler knows nothing about the SQL string literal embedded in `DbService.GetQueueAsync`; a refactor of the C# rule leaves the SQL silently parallel-implementing the old version.

The deeper principle: **rules have a *semantic identity* that's independent of the language they're expressed in**. Once a rule is named (e.g., "an episode is 'new' if it aired in the last 7 days"), every parallel implementation of it — SQL CASE, JavaScript helper, Razor template condition, AI-prompt instruction, even a comment that documents an expected behavior — is a consumer that must be audited together.

## How to audit semantic consumers

The mechanical "find all callers" tools find only one half of the surface. The other half lives in:

1. **SQL strings** — `Endpoints/*.cs`, `Services/*.cs`. Grep for the underlying column names the predicate touches (e.g., `LatestEpisodeDate`, `Status`) and read every `CASE`, `WHERE`, `ORDER BY` that mentions them.
2. **Razor templates** — `Pages/*.razor`, `Components/*.razor`. Grep for the column names and inline arithmetic that might re-implement the rule (e.g., a `@if (item.LatestEpisodeDate.HasValue && item.LatestEpisodeDate > DateTime.UtcNow.AddDays(-7))` would be a re-implementation).
3. **AI prompts** — `Services/AIPromptBuilder.cs` or wherever prompt strings live. A prompt that tells the model "an episode is new if it aired in the last week" is a semantic consumer.
4. **Comments** — comments that document the rule are *also* consumers in the sense that a refactor must keep them honest.
5. **Tests** — tests that pin the rule against specific dates are consumers; if the rule changes, the tests must change too.

The grep target is the **underlying column or signal**, not the predicate name. A predicate that hasn't been promoted yet is being parallel-implemented somewhere; grep `LatestEpisodeDate`, not `HasNewEpisode`.

## Example

When fixing #86 the right audit would have been:

```bash
# don't grep the predicate name
grep -r "HasNewEpisode" --include="*.cs" --include="*.razor"

# grep the underlying signal — this is what catches semantic consumers
grep -rn "LatestEpisodeDate" --include="*.cs" --include="*.razor"
```

The second command would have surfaced `DbService.cs:189` (the SQL freshness CASE) as a one-sided window that needed the same upper bound. The fix that ultimately landed in commit `bb37373` added `AND LatestEpisodeDate <= NOW()` to the SQL — the exact mirror of the C# `<= DateTime.UtcNow` fix.

## When this matters most

The audit step is cheapest at the moment of the rule's first promotion (or first bugfix). Once a rule lives in two implementations that drift apart, every future contributor has to figure out *which one is canonical* — and the answer is "they were supposed to be the same but aren't anymore." Promoting to Shared with a comment-pointer (`-- canonical: see HurrahTv.Shared.Models.QueueStatusOrdering.DisplayOrder`) is the cheapest forcing function: it makes the link grep-able even from inside a SQL string.
