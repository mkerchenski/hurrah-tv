# CA1861 Misapplies to xUnit Assertions — Suppress in Test Projects

> **Area:** Tooling | Build
> **Date:** 2026-05-22
> **Resolves:** mkerchenski/hurrah-tv#82 (surfaced during CI failure on PR #93)

## Context

Running `dotnet format analyzers HurrahTv.slnx --severity info --no-restore` on a PR that added `QueueItemExtensionsTests.cs` (16 facts using `Assert.Equal(new[] { 8, 9, 15 }, item.ParseAvailableOnProviderIds())`) produced two problematic outcomes:

1. **The analyzer auto-applied a fix** that hoisted `new[] { 8, 9, 15 }` into a class-level `private static readonly int[] expected = new[] { 8, 9, 15 };` field — used by exactly one test, with a generic lowercase name. The result was strictly worse code: the expected literal was no longer next to the assertion that referenced it.
2. **CI's `dotnet format --verify-no-changes` rejected the un-applied diagnostics** when I reverted the field hoist back to inline form, blocking the merge.

The rule is CA1861: "Avoid constant arrays as arguments. Prefer 'static readonly' fields over constant array arguments if the called method is called repeatedly and is not mutating the passed array."

## Learning

CA1861's premise — "called repeatedly, allocation-heavy" — is wrong for xUnit `[Fact]` and `[Theory]` methods. Each test runs **once per invocation**, not in a hot loop. Hoisting the expected literal away from the assertion site destroys the readability that `Assert.Equal(expected, actual)` is designed around: the entire xUnit idiom places the expected value next to the assertion so the test reads as a single thought.

**Suppress CA1861 for test projects via `.editorconfig`** rather than fighting the analyzer at every PR.

## Editorconfig glob trap (the actually-non-obvious part)

Editorconfig section globs are subtly different from gitignore globs. Three patterns failed before one worked:

| Pattern | Behavior |
|---|---|
| `[**/*Tests/**.cs]` | Didn't match — `**.cs` is ambiguous; the analyzer engine didn't interpret it as "any .cs in any subdir" |
| `[{HurrahTv.Shared.Tests,HurrahTv.Client.Tests,HurrahTv.Api.Tests}/**/*.cs]` | Didn't match — `**/*.cs` after the brace expansion requires at least one intermediate path segment between the project dir and the file. Our test files live directly in the project root (`HurrahTv.Shared.Tests/QueueItemExtensionsTests.cs`), so this glob effectively required `<project>/<subdir>/<file>.cs` and skipped flat files. |
| `[*Tests.cs]` | **Worked.** Basename-suffix pattern (no `/`) matches across all directories. Safe in this repo because no production file is named `*Tests.cs`. |

The deeper rule: when an editorconfig glob isn't matching, drop to a basename-only pattern (no `/`) before reaching for `**`. It's blunter but unambiguous. The risk is unintended matches in production code — verify with `grep -r "Tests.cs"` (or equivalent file listing) before committing the glob.

## Example

```ini
# .editorconfig
[*Tests.cs]
dotnet_diagnostic.CA1861.severity = none
```

This silences CA1861 for any file whose basename ends in `Tests.cs`, regardless of which directory it lives in. Production code keeps the rule (where the "called repeatedly" premise actually applies — e.g., a hot-path method with a constant array argument should hoist).

## Related gotcha: `dotnet format analyzers` is not always mechanical

Most `dotnet format` rules (whitespace, brace style, expression-bodied member preferences) are stylistic transformations that produce equivalent code. CA-rule autofixes are different: they can be **opinionated refactors** that change code organization (CA1861 hoists to fields; CA1822 makes methods static; etc.). Treat the `analyzers` sub-command's output as a **proposal**, not a mechanical reformatting. Always diff the changes before committing.
