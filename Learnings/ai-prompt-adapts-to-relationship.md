# AI Show Match Should Adapt Prompt to User's Relationship

> **Area:** AI | UX
> **Date:** 2026-04-05

## Context
The AI "match" blurb on the details page always asked "would this user enjoy this show?" — even for shows they're already 3 seasons into. This made the AI seem oblivious and the feature useless for items already in the user's list.

## Learning
The AI prompt must shift its role based on the user's relationship with the content:

| Status | AI role | Don't say |
|--------|---------|-----------|
| Not in list | "Would you like this?" | — |
| Watching | "Why this appeals to you" | "You'd enjoy this" |
| Finished + positive | "What this says about your taste" | "Try this show" |
| Finished + negative | "Why this didn't work for you" | "You'd love this" |
| Want to Watch | "Build excitement for it" | "Consider adding this" |
| Not For Me | "Why it's probably not for you" | "Give it a chance" |

The implementation uses a `BuildRoleInstruction()` method that pattern-matches on both `Status` and `Sentiment` to produce targeted prompt instructions. The prompt also includes episode-level sentiment data when available, so the AI can reference "you loved seasons 1-3 but the later seasons lost you."

## Architectural implication
Any AI feature that describes content to a user should check the user's existing relationship with that content first. The prompt framing matters more than the model — the same model with the wrong framing produces useless output.
