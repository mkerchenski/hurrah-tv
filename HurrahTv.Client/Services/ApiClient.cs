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
    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1) =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search?q={Uri.EscapeDataString(query)}&page={page}") ?? [];

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

    public async Task<QueueItem?> MarkAsLikedAsync(SearchResult result)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/queue/liked", new
        {
            result.TmdbId,
            result.MediaType,
            result.Title,
            result.PosterPath,
            AvailableOnJson = System.Text.Json.JsonSerializer.Serialize(result.AvailableOn.Select(s => s.ProviderId).ToList())
        });
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>();
        return null;
    }

    public async Task<QueueItem?> MarkAsSeenAsync(SearchResult result)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/queue/seen", new
        {
            result.TmdbId,
            result.MediaType,
            result.Title,
            result.PosterPath,
            AvailableOnJson = System.Text.Json.JsonSerializer.Serialize(result.AvailableOn.Select(s => s.ProviderId).ToList())
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
            Console.WriteLine("[ApiClient] Calling api/curation/rows...");
            HttpResponseMessage response = await _http.GetAsync("api/curation/rows");
            string body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[ApiClient] Status: {response.StatusCode}, Body length: {body.Length}, First 200: {body[..Math.Min(200, body.Length)]}");
            if (!response.IsSuccessStatusCode) return null;
            return System.Text.Json.JsonSerializer.Deserialize<CurationResponse>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApiClient] Curation error: {ex.Message}");
            return null;
        }
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
