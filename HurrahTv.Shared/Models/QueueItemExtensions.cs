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
