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

    public async Task<List<SearchResult>> ForYouAsync(string mediaType = "all", int[]? excludeIds = null)
    {
        string url = $"api/search/for-you?mediaType={mediaType}";
        if (excludeIds?.Length > 0) url += "&exclude=" + string.Join(",", excludeIds);
        return await _http.GetFromJsonAsync<List<SearchResult>>(url) ?? [];
    }

    public async Task<List<SearchResult>> NewOnServicesAsync(string mediaType = "tv", int[]? excludeIds = null)
    {
        string url = $"api/search/new?mediaType={mediaType}";
        if (excludeIds?.Length > 0) url += "&exclude=" + string.Join(",", excludeIds);
        return await _http.GetFromJsonAsync<List<SearchResult>>(url) ?? [];
    }

    public async Task<List<SearchResult>> GetRecommendationsAsync(int tmdbId, string mediaType) =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/recommendations/{mediaType}/{tmdbId}") ?? [];

    // details
    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType) =>
        await _http.GetFromJsonAsync<ShowDetails>($"api/details/{mediaType}/{tmdbId}");

    // season episodes
    public async Task<SeasonDetail?> GetSeasonAsync(int tmdbId, int seasonNumber) =>
        await _http.GetFromJsonAsync<SeasonDetail>($"api/details/tv/{tmdbId}/season/{seasonNumber}");

    // queue
    public async Task<QueueResponse> GetQueueResponseAsync()
    {
        QueueResponse? response = await _http.GetFromJsonAsync<QueueResponse>("api/queue");
        return response ?? new QueueResponse([], []);
    }

    // convenience for callers that only need items (Details page, Queue page)
    public async Task<List<QueueItem>> GetQueueAsync()
    {
        QueueResponse response = await GetQueueResponseAsync();
        return response.Items;
    }

    // watched episodes
    public async Task MarkEpisodeWatchedAsync(int tmdbId, int season, int episode) =>
        await _http.PostAsJsonAsync("api/episodes/watched", new WatchedEpisodeRequest(tmdbId, season, episode));

    public async Task UnmarkEpisodeWatchedAsync(int tmdbId, int season, int episode) =>
        await _http.DeleteAsync($"api/episodes/watched/{tmdbId}/{season}/{episode}");

    public async Task<List<WatchedEpisode>> GetWatchedEpisodesAsync(int tmdbId) =>
        await _http.GetFromJsonAsync<List<WatchedEpisode>>($"api/episodes/watched/{tmdbId}") ?? [];

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

    public async Task UpdateSentimentAsync(int id, int? sentiment) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/sentiment", new { Sentiment = sentiment });

    public async Task UpdateProgressAsync(int id, int? season, int? episode) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/progress", new { Season = season, Episode = episode });

    public Task<QueueItem?> MarkAsSeenAsync(SearchResult result) => PostStatusAsync("api/queue/seen", result);

    // returns the queue item for this content, creating as WantToWatch if absent.
    // never mutates existing — used by QuickActions when the dialog opens from a browse surface
    // that can't know whether the item is already queued. Caller follows up with UpdateStatus /
    // UpdateSentiment using the returned Id.
    public Task<QueueItem?> EnsureQueueItemAsync(SearchResult result) => PostStatusAsync("api/queue/ensure", result);

    // season & episode sentiments
    public async Task<ShowSentiments> GetShowSentimentsAsync(int tmdbId) =>
        await _http.GetFromJsonAsync<ShowSentiments>($"api/shows/{tmdbId}/sentiments") ?? new();

    public async Task SetSeasonSentimentAsync(int tmdbId, int seasonNumber, int? sentiment) =>
        await _http.PutAsJsonAsync($"api/shows/{tmdbId}/seasons/{seasonNumber}/sentiment", new { Sentiment = sentiment });

    public async Task SetEpisodeSentimentAsync(int tmdbId, int seasonNumber, int episodeNumber, int? sentiment) =>
        await _http.PutAsJsonAsync($"api/shows/{tmdbId}/seasons/{seasonNumber}/episodes/{episodeNumber}/sentiment", new { Sentiment = sentiment });

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

    // user settings
    public async Task<UserSettings> GetUserSettingsAsync() =>
        await _http.GetFromJsonAsync<UserSettings>("api/settings") ?? new UserSettings();

    public async Task SetUserSettingsAsync(UserSettings settings) =>
        await _http.PutAsJsonAsync("api/settings", settings);

    // user services
    public async Task<List<int>> GetUserServicesAsync() =>
        await _http.GetFromJsonAsync<List<int>>("api/services") ?? [];

    public async Task SetUserServicesAsync(List<int> providerIds) =>
        await _http.PutAsJsonAsync("api/services", providerIds);

    // user genres
    public async Task<List<int>> GetUserGenresAsync() =>
        await _http.GetFromJsonAsync<List<int>>("api/genres") ?? [];

    public async Task SetUserGenresAsync(List<int> genreIds) =>
        await _http.PutAsJsonAsync("api/genres", genreIds);

    // profile
    public async Task<UserProfile?> GetProfileAsync() =>
        await _http.GetFromJsonAsync<UserProfile>("api/profile");

    public async Task<AuthResponse?> UpdateProfileAsync(string? firstName)
    {
        HttpResponseMessage response = await _http.PutAsJsonAsync("api/profile", new UpdateProfileRequest(firstName));
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AuthResponse>();
    }

    // admin
    public async Task<AdminUsersResponse?> GetAdminUsersAsync() =>
        await _http.GetFromJsonAsync<AdminUsersResponse>("api/admin/users");

    public async Task<AdminUserDetail?> GetAdminUserDetailAsync(string userId) =>
        await _http.GetFromJsonAsync<AdminUserDetail>($"api/admin/users/{userId}");

    public async Task<HttpResponseMessage> SetUserAdminAsync(string userId, bool isAdmin) =>
        await _http.PostAsJsonAsync($"api/admin/users/{userId}/admin", new AdminSetAdminRequest(isAdmin));

    public async Task<HttpResponseMessage> SetUserFirstNameAsync(string userId, string? firstName) =>
        await _http.PutAsJsonAsync($"api/admin/users/{userId}/firstname", new AdminSetFirstNameRequest(firstName));

    public async Task<AdminAiUsageResponse?> GetAdminAiUsageAsync() =>
        await _http.GetFromJsonAsync<AdminAiUsageResponse>("api/admin/ai-usage");

    public async Task<AdminOnboardingFunnel?> GetAdminOnboardingAsync() =>
        await _http.GetFromJsonAsync<AdminOnboardingFunnel>("api/admin/onboarding");
}
