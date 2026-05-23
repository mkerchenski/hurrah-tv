# CI's `dotnet format --verify-no-changes` Catches Rules the Targeted Sub-Commands Miss

> **Area:** CI | Tooling
> **Date:** 2026-05-23

## Context

`/xreview` runs three sub-commands locally to apply formatter fixes:

```bash
dotnet format whitespace HurrahTv.slnx --no-restore --verbosity minimal
dotnet format style HurrahTv.slnx --severity info --no-restore --diagnostics IDE0008 ... IDE0305
dotnet format analyzers HurrahTv.slnx --severity info --no-restore --verbosity minimal
```

All three reported clean. CI then ran:

```bash
dotnet format --verify-no-changes --severity info --no-restore
```

…and failed twice in a row — first on `IDE0305` (`Collection initialization can be simplified` against `.ToArray()` chains), then on `IDE0330` (`Use 'System.Threading.Lock'` after introducing a new `private readonly object _lock = new()`). Both rules were live and applicable; the local targeted runs just didn't apply them.

Two CI cycles burned. The full diagnostic set the bare `dotnet format` command applies is a superset of what `dotnet format style --diagnostics …` applies, even when the same rule IDs appear in the `--diagnostics` list.

## Learning

**The bare `dotnet format` is not the union of `dotnet format whitespace`, `dotnet format style`, and `dotnet format analyzers`.** It runs a different rule set, with different codefix providers active, against the same source. Some IDE-prefix rules (IDE0305, IDE0330 among them) only fire under the bare command. Some style-category rules' codefix providers need the analyzers sub-command's host setup. Constraining via `--diagnostics` further filters away codefix providers that aren't pinned by ID — even if the ID is in the list.

The exact mechanism isn't documented clearly anywhere I could find, but the *behavioral* contract is reliable: **CI uses bare `dotnet format --verify-no-changes --severity info --no-restore`, so that's what local validation must match.**

Before pushing any C# change, run the same command CI runs:

```bash
dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx
```

Exit code 0 means CI's format gate will pass. Exit code 2 means it will fail; the output lists the file:line + rule ID. Apply the fix manually (the bare `dotnet format` without `--verify-no-changes` will write the fix back, but that's a writeback risk — review the diff before committing).

## Caveat: bare `dotnet format` can crash on IDE0130

`IDE0130` (`Namespace does not match folder structure`) has a codefix that renames files, which `MSBuildWorkspace` forbids — running the bare command can hit `System.NotSupportedException: Changing document properties is not supported` and abort before validating any other rules. The targeted sub-command in `/xreview` excludes IDE0130 from `--diagnostics` for that reason. The trade-off:

- **Targeted command**: dodges the IDE0130 crash but misses IDE0305 / IDE0330 / others.
- **Bare command**: catches everything CI does, but may crash on the codebase's specific layout.

Run bare first. If it crashes, fall back to the targeted command + manual fix of the crashing rule.

## Example

```bash
# CI gate (run before every push)
dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx

# If the gate complains about IDE0305 or similar:
# either apply manually (preferred — see the actual diff)
# or run the bare command without --verify-no-changes to write the fix
dotnet format --severity info --no-restore HurrahTv.slnx
git diff   # always review
```

## Operational note for `/xreview`

The xreview skill applies `dotnet format whitespace/style/analyzers` separately and reports them as "clean" — but that gives false confidence relative to CI. After running those sub-commands, also run the bare verify command as the final gate. Without that step, the CI failure mode reproduces every time the user adds code that triggers a rule the targeted run missed.
