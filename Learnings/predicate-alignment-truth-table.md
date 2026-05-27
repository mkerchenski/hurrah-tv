# Aligning Predicates: Use a Truth Table, and Don't Trust Copied Guards

> **Area:** Data
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#141

## Context

#141 was a client/API divergence: the Home watchlist filter (`IsStreamableOn`,
`HurrahTv.Shared/Models/QueueItemExtensions.cs`) hid queued shows whose
`AvailableOnJson` was empty, while the API queue read (`IsWatchableOn`,
`HurrahTv.Api/Endpoints/QueueEndpoints.cs`) kept them ("unknown providers — don't
hide"). The fix made the empty case match and shipped with tests.

A follow-up `/xreview` then ran a full truth table comparing the two predicates
across *every* input category, not just the reported one. It found a **second,
independent divergence** that the empty-case fix left untouched: the client gated
each provider match on `&& StreamingService.ById.ContainsKey(id)`, which the API
never did. A provider id present in both the user's services and an item's
`AvailableOnJson` but absent from the hardcoded `ById` registry (e.g. a TMDb
provider outside our eight) would be kept by the API and hidden by the client —
the *same class of bug* #141 was filed to fix, hiding in the same method.

## Learning

Two things that aren't obvious until a divergence bites twice:

1. **"Align predicate X to predicate Y" is only finished when a truth table over
   every input category agrees.** The reported symptom (empty providers) is one
   row of that table. Enumerate the rest — null, malformed, known-match,
   known-no-match, empty-collection, and *out-of-registry* — and check each pair.
   The cheap audit for an intra-language alignment is the truth table; for the
   cross-*language* case (a SQL `CASE` or Razor condition re-implementing the same
   rule) see `Learnings/shared-promotion-semantic-audit.md`.

2. **A guard that is correct in one predicate is often wrong when copy-pasted into
   another that asks a different question of the same data.** The
   `ById.ContainsKey` guard is *correct* in `VisibleServicesFor` — that method
   renders service logos, and you can't render a logo for a provider the registry
   doesn't know. It is *wrong* in `IsStreamableOn` — a visibility predicate, where
   a show is still streamable on a service the user has even if our registry
   doesn't list that provider. Same field (`AvailableOnJson`), different question
   ("which logos?" vs "hide y/n?"), so the guard doesn't transfer. When you see a
   guard repeated across sibling helpers, ask what question each one answers before
   assuming it belongs in both.

## Example

The residual divergence and its fix (the empty-case fix from #141 was already in
place; this is the second alignment):

```csharp
// before — borrowed from VisibleServicesFor, wrong here:
if (userServices.Contains(id) && StreamingService.ById.ContainsKey(id))
    return true;

// after — plain membership, matching the API's IsWatchableOn:
if (userServices.Contains(id))
    return true;
```

Truth table that proved the alignment complete (non-empty userServices unless noted):

| AvailableOnJson | userServices | API IsWatchableOn | Client IsStreamableOn (after) |
|---|---|---|---|
| `null` / `""` / `"[]"` / malformed | any | true | true |
| `[8]` (known) | `[8]` | true | true |
| `[8]` (known) | `[9]` | false | false |
| `[999]` (out-of-registry) | `[999]` | true | **true** (was false) |
| any | `[]` | n/a (API skips filter) | false (by-design guard, kept) |

The empty-`userServices` row is a deliberate, documented asymmetry, not a bug — the
API skips filtering entirely when no services are selected, the client returns
false, and the only client call site can't reach that state with a non-empty queue.
