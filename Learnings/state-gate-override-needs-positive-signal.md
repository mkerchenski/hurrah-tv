# A State-Gate Override Must Key on Positive Evidence, Not a Broad Proxy

> **Area:** Data | UI
> **Date:** 2026-06-02
> **Resolves:** mkerchenski/hurrah-tv#170

## Context

The Home "Available Now" filter hides a show once you've watched its latest episode
(`!IsLatestEpisodeWatched`). #145 added a Watching-only *override* that re-surfaces a
caught-up show when a newer episode is plausibly out, because TMDb's date-only `air_date`
plus a 12h refresh can lag behind reality:

```csharp
bool overrideLatestWatched = isWatching
    && item.LatestEpisodeDate is { } led
    && led.Date < today;            // <- the trap
```

`LatestEpisodeDate.Date < today` is true for essentially **every** show whose latest episode
aired before today — i.e. almost all of them. So the override fired constantly and the
`IsLatestEpisodeWatched` gate it was bypassing became a no-op: marking the latest episode
watched never removed a Watching show from Available Now (#170). It only dropped if the latest
episode aired *literally today*.

## Learning

1. **An override/escape-hatch that bypasses a primary gate must key on *positive evidence* of
   the specific condition it exists to handle — not a broad proxy that happens to be true for
   most rows.** The override's job was "resurface only when a genuinely newer episode has
   aired." The precise signal for that is `NextEpisodeDate` having passed
   (`NextEpisodeDate.Date <= today`), not "the last episode aired on a prior day." The broad
   proxy didn't *narrow* the gate — it nullified it. When you add a bypass, ask: "for what
   fraction of rows is this true?" If the answer is "nearly all," it's not a special case, it's
   a silent override of the rule.

2. **Test the dimension the override is gated on.** The regression shipped green because the
   existing `AvailableNow_Excludes_LatestEpisode_Already_Watched` test used `TvItem`'s default
   status (`WantToWatch`), and the override only fires for `Watching`. The
   `Watching`-watched-stale branch — the exact path #145 introduced and #170 broke — had **no**
   test. When a predicate grows a branch gated by some dimension (here `status == Watching`), a
   regression test must set that dimension, or the branch is unverified no matter how many other
   tests pass.

3. **A fix that clears the immediately-reported symptom can still encode a deeper wrong
   assumption.** #145 was itself a Copilot-praised fix for a *different* same-day bug
   ([[tmdb-air-date-is-date-only]]), and its truth table labeled the weekly-show-stays-visible
   case an "acceptable false-positive." That "acceptable" false-positive *was* the bug a user
   reported as #170. Verify a fix's premise, not just that the originally-reported case is now
   handled — a reviewer (even a sharp one) can bless a predicate that's still wrong.

## Example

The #170 fix — resurface only on the positive signal:

```csharp
// resurface a caught-up show ONLY when a genuinely newer episode has aired that the 12h
// refresh hasn't folded into LatestEpisode* yet — i.e. NextEpisodeDate's air day has arrived.
bool overrideLatestWatched = isWatching
    && item.NextEpisodeDate is { } nextAired
    && nextAired.Date <= today;
```

This is complementary to the AvailableLater branch (`next.Date > today`), so the two never
gap or overlap at `today`. A daily show (Kimmel) keeps working — its next episode airs today;
a weekly show you've caught up on correctly drops.

Related: [[tmdb-air-date-is-date-only]], [[date-predicates-prefer-typed-comparisons]],
[[predicate-alignment-truth-table]].
