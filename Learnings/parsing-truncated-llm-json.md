# Salvage Truncated LLM JSON Arrays Instead of Discarding Them

> **Area:** API | AI
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#135

## Context
The AI curator returns a JSON array of picks (`[{"id":..,"score":..,"reason":".."}]`). When the reservoir grew from 12–15 short-reason picks to 25–30 with 2–3 sentence reasons, the response occasionally hit the `MaxTokens` cap and got cut off mid-array — no closing `]`.

## Learning
The naive extract-the-array parse silently discards the *entire* response on truncation:

```csharp
int jsonStart = text.IndexOf('[');
int jsonEnd = text.LastIndexOf(']');   // -1 when truncated → no closing bracket
if (jsonStart >= 0 && jsonEnd > jsonStart)
    text = text[jsonStart..(jsonEnd + 1)];
// else: text stays as the raw truncated string → Deserialize throws → caught → [] → no result
```

So a truncated response = empty reservoir = no recommendation, even though 25+ complete pick objects were present in the text. The inference was paid for and the user got nothing.

Fix: when there's an opening `[` but no closing `]`, salvage the complete leading objects by closing the array after the last finished one:

```csharp
else if (jsonStart >= 0)
{
    int lastObject = text.LastIndexOf('}');
    if (lastObject > jsonStart)
        text = text[jsonStart..(lastObject + 1)] + "]";
}
```

Two defenses still belong alongside it: keep the `Deserialize` in a try/catch that degrades to `[]` (a `}` inside an unterminated string is still possible), and never cache an empty result (see `curation-cache-gotchas.md`) so a one-off truncation doesn't freeze. And raise `MaxTokens` to fit the expected worst-case output — salvage is the safety net, not the plan.

## File pointers
- `HurrahTv.Api/Services/CurationService.cs` — `CurateWithAIAsync` JSON extraction + salvage branch
