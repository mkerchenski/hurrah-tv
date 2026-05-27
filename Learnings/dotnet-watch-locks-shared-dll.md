# A Running `dotnet watch` Locks Shared.dll — Stop Dev Servers Before `dotnet test`

> **Area:** Deployment
> **Date:** 2026-05-27

## Context

While iterating on PR #142 on Windows, both dev servers were running via
`dotnet watch` (the CLAUDE.md "Running Locally" flow). A `dotnet test HurrahTv.slnx`
issued in parallel failed the rebuild with:

```
error MSB3027: Could not copy "...\HurrahTv.Shared\bin\Debug\net10.0\HurrahTv.Shared.dll"
to "bin\Debug\net10.0\HurrahTv.Shared.dll". Exceeded retry count of 10. Failed.
The file is locked by: "HurrahTv.Api (31600)"
```

The test projects that had already built passed; only the projects whose output
the running app holds open failed to copy.

## Learning

On Windows, the live `HurrahTv.Api` (and `HurrahTv.Client`) host process started by
`dotnet watch` keeps `HurrahTv.Shared.dll` open. A concurrent full-solution build or
`dotnet test` can't overwrite that DLL, so MSBuild retries and then fails with
MSB3027/MSB3021 — a **file lock, not a compile error**. `dotnet watch` will have
already hot-reloaded the source change into the running process, which is why the
app behaves correctly even though the out-of-band build fails.

Before running `dotnet test` / `dotnet build` against the whole solution on Windows,
stop the dev servers. One-liner to kill just this repo's watch + host processes:

```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='HurrahTv.Api.exe' OR Name='HurrahTv.Client.exe'" |
  Where-Object { $_.CommandLine -match 'HurrahTv|dotnet-watch' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

This is platform-specific: on macOS/Linux the loader doesn't hold an exclusive
write lock, so the same parallel `dotnet test` generally succeeds. The symptom is
Windows-only, which is why it isn't in the (cross-platform) CLAUDE.md test notes.
