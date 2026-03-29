using System.Net.Http.Json;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

public class ApiClient(HttpClient http)
{
    private readonly HttpClient _http = http;

    // auth
    public async Task<bool> SendCodeAsync(string phoneNumber)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/send-code", new SendCodeRequest(phoneNumber));
        return response.IsSuccessStatusCode;
    }

    public async Task<AuthResponse?> VerifyCodeAsync(string phoneNumber, string code)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/verify", new VerifyCodeRequest(phoneNumber, code));
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<AuthResponse>();
        return null;
    }

    // search
    public async Task<SearchResponse> SearchAsync(string query) =>
        await _http.GetFromJsonAsync<SearchResponse>($"api/search?q={Uri.EscapeDataString(query)}")
        ?? new SearchResponse();

    public async Task<List<SearchResult>> ForYouAsync(string mediaType = "all") =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/for-you?mediaType={mediaType}") ?? [];

    public async Task<List<SearchResult>> NewOnServicesAsync(string mediaType = "tv") =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/new?mediaType={mediaType}") ?? [];

    public async Task<List<SearchResult>> GetRecommendationsAsync(int tmdbId, string mediaType) =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/recommendations/{mediaType}/{tmdbId}") ?? [];

    // details
    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType) =>
        await _http.GetFromJsonAsync<ShowDetails>($"api/details/{mediaType}/{tmdbId}");

    // queue
    public async Task<List<QueueItem>> GetQueueAsync() =>
        await _http.GetFromJsonAsync<List<QueueItem>>("api/queue") ?? [];

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/queue", item);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>();
        return null;
    }

    public async Task RemoveFromQueueAsync(int id) =>
        await _http.DeleteAsync($"api/queue/{id}");

    public async Task UpdateStatusAsync(int id, QueueStatus status) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/status", new { Status = status });

    public async Task UpdateRatingAsync(int id, int? rating) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/rating", new { Rating = rating });

    public async Task UpdateProgressAsync(int id, int? season, int? episode) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/progress", new { Season = season, Episode = episode });

    public Task<QueueItem?> MarkAsLikedAsync(SearchResult result) => PostStatusAsync("api/queue/liked", result);
    public Task<QueueItem?> MarkAsSeenAsync(SearchResult result) => PostStatusAsync("api/queue/seen", result);

    private async Task<QueueItem?> PostStatusAsync(string endpoint, SearchResult result)
    {
        QueueItem qi = result.ToQueueItem();
        HttpResponseMessage response = await _http.PostAsJsonAsync(endpoint, new
        {
            result.TmdbId,
            result.MediaType,
            result.Title,
            result.PosterPath,
            qi.AvailableOnJson
        });
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>();
        return null;
    }

    // curation
    public async Task<CurationResponse?> GetCuratedRowsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<CurationResponse>("api/curation/rows");
        }
        catch { return null; }
    }

    public async Task<CurationResponse?> RefreshCurationAsync()
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsync("api/curation/refresh", null);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<CurationResponse>();
            return null;
        }
        catch { return null; }
    }

    public async Task<ShowMatchResult?> GetShowMatchAsync(int tmdbId, string mediaType)
    {
        try
        {
            return await _http.GetFromJsonAsync<ShowMatchResult?>($"api/curation/match/{mediaType}/{tmdbId}");
        }
        catch { return null; }
    }

    // user services
    public async Task<List<int>> GetUserServicesAsync() =>
        await _http.GetFromJsonAsync<List<int>>("api/services") ?? [];

    public async Task SetUserServicesAsync(List<int> providerIds) =>
        await _http.PutAsJsonAsync("api/services", providerIds);

    // dismissals
    public async Task DismissAsync(int tmdbId) =>
        await _http.PostAsync($"api/dismissals/{tmdbId}", null);

    public async Task ClearDismissalsAsync() =>
        await _http.DeleteAsync("api/dismissals");

    // user genres
    public async Task<List<int>> GetUserGenresAsync() =>
        await _http.GetFromJsonAsync<List<int>>("api/genres") ?? [];

    public async Task SetUserGenresAsync(List<int> genreIds) =>
        await _http.PutAsJsonAsync("api/genres", genreIds);
}
