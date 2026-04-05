# Orthogonal Data Dimensions — Separate Status from Sentiment

> **Area:** Data | Architecture
> **Date:** 2026-04-05

## Context
The original `QueueStatus` enum conflated two concepts: where an item sits in your list (Watching, Watched, WantToWatch) and how you feel about it (Liked). This made it impossible to express "I'm watching this but I hate it" or "I finished this and it was just okay." The `Liked` status implied both "finished" and "loved."

## Learning
When a data field encodes two independent dimensions, split them. List position (where it sits) and sentiment (how you feel) are orthogonal — you can be watching something you dislike, or have finished something with no strong opinion. The refactor:

- **Status** (enum): WantToWatch, Watching, Finished, NotForMe — where the item lives
- **Sentiment** (nullable int): null, Down(1), Up(2), Favorite(3) — how you feel, independently

This separation enabled:
1. Episode-level and season-level sentiment (same 3-level scale at each granularity)
2. Richer AI taste profiles (sentiment is strongest signal, status is weaker fallback)
3. Context-aware AI prompts that adapt based on the user's relationship with content
4. Simpler UI — list buttons and sentiment buttons are visually and functionally separate

The migration was: `UPDATE QueueItems SET Sentiment = 3, Status = 2 WHERE Status = 3` (Liked → Finished + Favorite).

## Architectural implication
Any new "opinion" dimension should be a separate column, not a new enum value in Status. Status answers "what are you doing with this?" and Sentiment answers "how do you feel about it?"
