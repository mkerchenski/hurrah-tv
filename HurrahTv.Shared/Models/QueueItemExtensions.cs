using System.Text.Json;

namespace HurrahTv.Shared.Models;

public static class QueueItemExtensions
{
    public static List<int> ParseAvailableOnProviderIds(this QueueItem item)
    {
        if (string.IsNullOrEmpty(item.AvailableOnJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<int>>(item.AvailableOnJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // UTC calendar-day diffs are load-bearing for episode date logic — Postgres TIMESTAMPTZ
    // is Kind=Utc, so both operands must be UTC dates to avoid the Kind-mismatch drift that
    // bit us in #49/#70. Centralized here so Home filters and WatchlistRow badges can't drift.
    public static int? DaysUntilNextEpisode(this QueueItem item, DateTime todayUtc) =>
        item.NextEpisodeDate is { } d ? (int)(d.Date - todayUtc.Date).TotalDays : null;

    public static int? DaysSinceLatestEpisode(this QueueItem item, DateTime todayUtc) =>
        item.LatestEpisodeDate is { } d ? (int)(todayUtc.Date - d.Date).TotalDays : null;

    // streamable y/n for the Home watchlist filter — mirrors the API's IsWatchableOn
    // (QueueEndpoints) exactly: empty/unknown provider data is "don't hide" (#141), and a
    // match is plain service-id membership. NOT the same rule as the Queue page's
    // per-service chip filter, which hides empty-provider items because it answers a
    // narrower question ("is this demonstrably on THIS service" — see Queue.razor).
    public static bool IsStreamableOn(this QueueItem item, IReadOnlyList<int> userServices)
    {
        if (userServices.Count == 0) return false; // guard order matters: must precede the empty-providers check below
        List<int> providerIds = item.ParseAvailableOnProviderIds();
        if (providerIds.Count == 0) return true; // unknown providers — don't hide
        foreach (int id in providerIds)
        {
            // membership only — no StreamingService.ById gate, matching IsWatchableOn. a
            // provider the registry doesn't know is still streamable if the user has it.
            if (userServices.Contains(id))
                return true;
        }
        return false;
    }

    // returns known streaming services the user subscribes to, in provider-ID order
    // from the QueueItem's stored list, capped at max. Unknown providers are skipped.
    public static List<StreamingService> VisibleServicesFor(
        this QueueItem item,
        IReadOnlyList<int> userServices,
        int max = 2)
    {
        if (userServices.Count == 0) return [];
        List<int> parsed = item.ParseAvailableOnProviderIds();
        List<StreamingService> visible = [];
        foreach (int id in parsed)
        {
            if (visible.Count >= max) break;
            if (!userServices.Contains(id)) continue;
            if (!StreamingService.ById.TryGetValue(id, out StreamingService? svc)) continue;
            visible.Add(svc);
        }
        return visible;
    }
}
