# `catch (OCE) { throw; }` Without a Filter Conflates Timeout with Cancellation

> **Area:** WASM | API
> **Date:** 2026-05-24
> **Resolves:** mkerchenski/hurrah-tv#115, mkerchenski/hurrah-tv#117

## Context
PR #118 threaded `CancellationToken` through every `ApiClient` method and the curation `/match` endpoint. Three curation methods (`GetCuratedRowsAsync`, `RefreshCurationAsync`, `GetShowMatchAsync`) had a legacy bare `catch { return null; }` — a "network errors return null" contract that pages relied on for graceful fallback. The first cut added `catch (OperationCanceledException) { throw; }` *before* the bare catch so cancellation could propagate.

Copilot's PR review and three of five recall-mode reviewer agents independently flagged it as a regression. The OCE catch was unfiltered.

## Learning
`HttpClient.Timeout` raises `TaskCanceledException` — a subclass of `OperationCanceledException` — even when no caller-supplied token was cancelled. So an unfiltered `catch (OperationCanceledException) { throw; }` catches:

1. Caller-driven cancellation (the intent — propagate so the caller can abandon stale work)
2. HttpClient's internal timeout (NOT the intent — should keep returning `null` per the legacy contract so existing no-token callers don't suddenly see exceptions)

The `when` filter on the caller's token is the load-bearing piece:

```csharp
// BAD — rethrows on HttpClient.Timeout too, breaking no-token callers
catch (OperationCanceledException) { throw; }
catch { return null; }

// GOOD — only caller-driven cancellation propagates; everything else
// (timeouts, network errors, parse errors) still falls into the legacy
// null-return path
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
catch { return null; }
```

Same pattern matters one layer up in the caller. `Details.razor.LoadShowMatch` had `catch (OperationCanceledException)` without the filter — a timeout would have hit it and short-circuited before `_matchLoading = false` ran, leaving the spinner stuck. Same fix: `when (ct.IsCancellationRequested)`.

The trap is recurring because:
- The bare catch already existed — adding the OCE clause feels like a strict refinement, but it isn't.
- `TaskCanceledException : OperationCanceledException` is a quiet inheritance; the rethrow looks like it's about "the cancellation case" but the type covers both.
- Tests don't catch it unless they simulate `HttpClient.Timeout` (most tests use real `HttpClient` against a fast localhost or `WebApplicationFactory`).

## Related
- `api-await-with-timeout.md` — the inverse filter pattern (`catch (Exception ex) when (ex is not OperationCanceledException)`) for the same reason
- `blazor-wasm-async-event-exceptions.md` — why Blazor swallows escaping exceptions silently in async event handlers, amplifying the cost of misclassified cancellation
