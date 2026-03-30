# DateTime.Now vs DateTime.UtcNow in Blazor WASM

> **Area:** WASM
> **Date:** 2026-03-30

## Context
Built air date badges for the New Episodes row (e.g., "Today", "Yesterday", "Thu"). Initially used `DateTime.UtcNow` for all date math — review caught that this shows wrong day labels for users in western timezones.

## Learning
In Blazor WebAssembly, `DateTime.Now` returns the **browser's local time** (it runs in the browser runtime, which has access to the OS timezone). This is different from server-side Blazor or ASP.NET where `DateTime.Now` returns the server's timezone.

**Rule of thumb:**
- **Display-facing date labels** (badges, "aired yesterday", relative time): use `DateTime.Now` — matches what the user expects
- **Server comparisons** (cache staleness, API timestamps, DB queries): use `DateTime.UtcNow` — consistent across all clients

The tricky part: TMDb air dates are calendar dates with no timezone (e.g., `"2026-03-28"`). When parsed, they become `DateTime` with `Kind = Unspecified`. Comparing these against `DateTime.UtcNow.Date` can be off by one day for users in UTC-5 or later, because UTC midnight flips before their local midnight. Using `DateTime.Now.Date` in WASM aligns the comparison with the user's perception of "today."

## Example
```csharp
// In WASM component — user-facing badge
DateTime now = DateTime.Now; // browser's local time
int daysAgo = (int)(now.Date - episodeDate.Date).TotalDays;

// In API/server — cache staleness check
bool stale = DateTime.UtcNow - lastCheck > TimeSpan.FromHours(12);
```
