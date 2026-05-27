# A Partial-DTO PUT Against a Full-Row Upsert Silently Resets the Unsent Columns

> **Area:** Data | API | UI
> **Date:** 2026-05-27
> **Resolves:** mkerchenski/hurrah-tv#102

## Context

`/settings` only edits one field of `UserSettings` — `EnglishOnly` — so both the old Save button and
the new auto-save sent `new UserSettings { EnglishOnly = value }`. But `UserSettings` grew five more
columns over time (`ShowWatching`, `ShowWantToWatch`, `ShowFinished`, `WatchlistSort`, `MediaType`),
all owned by *other* surfaces (the Home watchlist filters, the MediaType nav tab). And
`SaveUserSettingsAsync` is a **full-row upsert** (`INSERT … ON CONFLICT DO UPDATE SET` on every
column). So PUT-ing a partial DTO wrote C# defaults into the five fields the Settings page never
loaded — silently resetting the user's home filters, sort order, and media-type tab.

The Copilot review and the API-safety reviewer agent both caught it; the local simplify pass and the
author's manual test did not (you only see it if you've customized one of the *other* surfaces first,
then touch Settings).

## Learning

When a screen edits a **subset** of a DTO whose API **upserts the whole row**, constructing a fresh
DTO with only the edited field (`new Dto { OneField = x }`) clobbers every other column with its C#
default. The mismatch is invisible at the call site — the type is satisfied, it compiles, and it
round-trips JSON fine. The fix is fetch-mutate-PUT: load the full current row, mutate the one field,
send all of it back (or carry the loaded values forward in the snapshot).

```csharp
// BUG: resets the five columns this page never loaded
Api.SetUserSettingsAsync(new UserSettings { EnglishOnly = value });

// FIX: carry the loaded values forward so only EnglishOnly changes
_settings.EnglishOnly = value;
Api.SetUserSettingsAsync(new UserSettings
{
    EnglishOnly = _settings.EnglishOnly,
    ShowWatching = _settings.ShowWatching,   // …and the rest, from the loaded row
    /* … */
});
```

**Auto-save amplifies this class of latent bug.** Under a deliberate Save button the overwrite fired
once, on an explicit user action — rare and easy to miss in review. Converting to per-control
auto-save fires it on *every* toggle, turning an occasional footgun into a constant one. When you
make a save implicit, re-audit what each save actually writes — the blast radius of any pre-existing
write bug just widened to "every interaction."

The deeper rule (cf. memory *audit every surface new state touches*): a shared persistence shape has
more owners than the screen in front of you. Before sending it, ask which *other* surfaces write the
same row, and whether your payload carries their fields intact.

## Related
- [[orthogonal-data-dimensions]] — when independent settings share one persisted row
- [[shared-promotion-semantic-audit]] — auditing all consumers/writers of a shared shape
