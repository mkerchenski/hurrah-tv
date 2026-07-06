using System.Text.Json;
using Microsoft.JSInterop;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

// Caches the last-shown Home hero in localStorage (#229). Two readers share the key:
//   1. this service — Home seeds _heroPick from it on init so the (already-preloading) backdrop
//      paints on first render instead of waiting for the /hero round-trip.
//   2. the pre-boot inline script in index.html — reads the same JSON before WASM boots and
//      injects <link rel="preload" as="image"> so the LCP image downloads in parallel with boot.
// Stored camelCase (JsonSerializerOptions.Web) so the plain-JS reader can walk result.backdropPath.
public class HeroCache(IJSRuntime js)
{
    // bump the suffix if the stored shape changes so stale entries are ignored, not mis-parsed.
    private const string Key = "hurrah_hero_v1";

    public async Task<CuratedHero?> GetAsync()
    {
        try
        {
            string? raw = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<CuratedHero>(raw, JsonSerializerOptions.Web);
        }
        catch
        {
            // best-effort cache — a parse/interop failure just means no seed this load.
            return null;
        }
    }

    public async Task SetAsync(CuratedHero hero)
    {
        try
        {
            string json = JsonSerializer.Serialize(hero, JsonSerializerOptions.Web);
            await js.InvokeVoidAsync("localStorage.setItem", Key, json);
        }
        catch
        {
            // non-critical: the pre-boot preload just won't have the newest pick next load.
        }
    }
}
