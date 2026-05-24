using System.Net.Http.Json;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

public class ApiClient(HttpClient http)
{
    private readonly HttpClient _http = http;

    // auth
    public async Task<bool> SendCodeAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/send-code", new SendCodeRequest(phoneNumber), cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<AuthResponse?> VerifyCodeAsync(string phoneNumber, string code, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/verify", new VerifyCodeRequest(phoneNumber, code), cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken);
        return null;
    }

    // search
    public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<SearchResponse>($"api/search?q={Uri.EscapeDataString(query)}", cancellationToken)
        ?? new SearchResponse();

    public async Task<List<SearchResult>> ForYouAsync(string mediaType = "all", int[]? excludeIds = null, CancellationToken cancellationToken = default)
    {
        string url = $"api/search/for-you?mediaType={mediaType}";
        if (excludeIds?.Length > 0) url += "&exclude=" + string.Join(",", excludeIds);
        return await _http.GetFromJsonAsync<List<SearchResult>>(url, cancellationToken) ?? [];
    }

    public async Task<List<SearchResult>> NewOnServicesAsync(string mediaType = "tv", int[]? excludeIds = null, CancellationToken cancellationToken = default)
    {
        string url = $"api/search/new?mediaType={mediaType}";
        if (excludeIds?.Length > 0) url += "&exclude=" + string.Join(",", excludeIds);
        return await _http.GetFromJsonAsync<List<SearchResult>>(url, cancellationToken) ?? [];
    }

    public async Task<List<SearchResult>> GetRecommendationsAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/recommendations/{mediaType}/{tmdbId}", cancellationToken) ?? [];

    // details
    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<ShowDetails>($"api/details/{mediaType}/{tmdbId}", cancellationToken);

    // season episodes
    public async Task<SeasonDetail?> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<SeasonDetail>($"api/details/tv/{tmdbId}/season/{seasonNumber}", cancellationToken);

    // queue
    public async Task<QueueResponse> GetQueueResponseAsync(CancellationToken cancellationToken = default)
    {
        QueueResponse? response = await _http.GetFromJsonAsync<QueueResponse>("api/queue", cancellationToken);
        return response ?? new QueueResponse([], []);
    }

    // convenience for callers that only need items (Details page, Queue page)
    public async Task<List<QueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        QueueResponse response = await GetQueueResponseAsync(cancellationToken);
        return response.Items;
    }

    // watched episodes
    public async Task MarkEpisodeWatchedAsync(int tmdbId, int season, int episode, CancellationToken cancellationToken = default) =>
        await _http.PostAsJsonAsync("api/episodes/watched", new WatchedEpisodeRequest(tmdbId, season, episode), cancellationToken);

    public async Task UnmarkEpisodeWatchedAsync(int tmdbId, int season, int episode, CancellationToken cancellationToken = default) =>
        await _http.DeleteAsync($"api/episodes/watched/{tmdbId}/{season}/{episode}", cancellationToken);

    public async Task<List<WatchedEpisode>> GetWatchedEpisodesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<WatchedEpisode>>($"api/episodes/watched/{tmdbId}", cancellationToken) ?? [];

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/queue", item, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>(cancellationToken);
        return null;
    }

    public async Task RemoveFromQueueAsync(int id, CancellationToken cancellationToken = default) =>
        await _http.DeleteAsync($"api/queue/{id}", cancellationToken);

    public async Task<QueueItem?> UpdateStatusAsync(int id, QueueStatus status, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage res = await _http.PutAsJsonAsync($"api/queue/{id}/status", new QueueStatusUpdate(status), cancellationToken);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<QueueItem>(cancellationToken) : null;
    }

    public async Task<QueueItem?> UpdateSentimentAsync(int id, int? sentiment, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage res = await _http.PutAsJsonAsync($"api/queue/{id}/sentiment", new SentimentUpdate(sentiment), cancellationToken);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<QueueItem>(cancellationToken) : null;
    }

    public async Task<bool> UpdateQueuePositionAsync(int id, int position, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage res = await _http.PutAsJsonAsync($"api/queue/{id}/position", new PositionUpdate(position), cancellationToken);
        return res.IsSuccessStatusCode;
    }

    public async Task<QueueItem?> UpdateProgressAsync(int id, int? season, int? episode, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage res = await _http.PutAsJsonAsync($"api/queue/{id}/progress", new ProgressUpdate(season, episode), cancellationToken);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<QueueItem>(cancellationToken) : null;
    }

    public Task<QueueItem?> MarkAsSeenAsync(SearchResult result, CancellationToken cancellationToken = default) =>
        PostStatusAsync("api/queue/seen", result, cancellationToken);

    // returns the queue item for this content, creating as WantToWatch if absent.
    // never mutates existing — used by QuickActions when the dialog opens from a browse surface
    // that can't know whether the item is already queued. Caller follows up with UpdateStatus /
    // UpdateSentiment using the returned Id.
    public Task<QueueItem?> EnsureQueueItemAsync(SearchResult result, CancellationToken cancellationToken = default) =>
        PostStatusAsync("api/queue/ensure", result, cancellationToken);

    // season & episode sentiments
    public async Task<ShowSentiments> GetShowSentimentsAsync(int tmdbId, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<ShowSentiments>($"api/shows/{tmdbId}/sentiments", cancellationToken) ?? new();

    public async Task SetSeasonSentimentAsync(int tmdbId, int seasonNumber, int? sentiment, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync($"api/shows/{tmdbId}/seasons/{seasonNumber}/sentiment", new { Sentiment = sentiment }, cancellationToken);

    public async Task SetEpisodeSentimentAsync(int tmdbId, int seasonNumber, int episodeNumber, int? sentiment, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync($"api/shows/{tmdbId}/seasons/{seasonNumber}/episodes/{episodeNumber}/sentiment", new { Sentiment = sentiment }, cancellationToken);

    private async Task<QueueItem?> PostStatusAsync(string endpoint, SearchResult result, CancellationToken cancellationToken = default)
    {
        QueueItem qi = result.ToQueueItem();
        SeenRequest request = new(result.TmdbId, result.MediaType, result.Title, result.PosterPath, qi.AvailableOnJson, result.BackdropPath);
        HttpResponseMessage response = await _http.PostAsJsonAsync(endpoint, request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>(cancellationToken);
        return null;
    }

    // curation
    public async Task<CurationResponse?> GetCuratedRowsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<CurationResponse>("api/curation/rows", cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<CurationResponse?> RefreshCurationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsync("api/curation/refresh", null, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<CurationResponse>(cancellationToken);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<ShowMatchResult?> GetShowMatchAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ShowMatchResult?>($"api/curation/match/{mediaType}/{tmdbId}", cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // user settings
    public async Task<UserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<UserSettings>("api/settings", cancellationToken) ?? new UserSettings();

    public async Task SetUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync("api/settings", settings, cancellationToken);

    // user services
    public async Task<List<int>> GetUserServicesAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<int>>("api/services", cancellationToken) ?? [];

    public async Task SetUserServicesAsync(List<int> providerIds, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync("api/services", providerIds, cancellationToken);

    // user genres
    public async Task<List<int>> GetUserGenresAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<int>>("api/genres", cancellationToken) ?? [];

    public async Task SetUserGenresAsync(List<int> genreIds, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync("api/genres", genreIds, cancellationToken);

    // profile
    public async Task<UserProfile?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<UserProfile>("api/profile", cancellationToken);

    public async Task<AuthResponse?> UpdateProfileAsync(string? firstName, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await _http.PutAsJsonAsync("api/profile", new UpdateProfileRequest(firstName), cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken);
    }

    // admin
    public async Task<AdminUsersResponse?> GetAdminUsersAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<AdminUsersResponse>("api/admin/users", cancellationToken);

    public async Task<AdminUserDetail?> GetAdminUserDetailAsync(string userId, CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<AdminUserDetail>($"api/admin/users/{userId}", cancellationToken);

    public async Task<HttpResponseMessage> SetUserAdminAsync(string userId, bool isAdmin, CancellationToken cancellationToken = default) =>
        await _http.PostAsJsonAsync($"api/admin/users/{userId}/admin", new AdminSetAdminRequest(isAdmin), cancellationToken);

    public async Task<HttpResponseMessage> SetUserFirstNameAsync(string userId, string? firstName, CancellationToken cancellationToken = default) =>
        await _http.PutAsJsonAsync($"api/admin/users/{userId}/firstname", new AdminSetFirstNameRequest(firstName), cancellationToken);

    public async Task<HttpResponseMessage> DeleteUserAsync(string userId, CancellationToken cancellationToken = default) =>
        await _http.DeleteAsync($"api/admin/users/{userId}", cancellationToken);

    public async Task<AdminAiUsageResponse?> GetAdminAiUsageAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<AdminAiUsageResponse>("api/admin/ai-usage", cancellationToken);

    public async Task<AdminOnboardingFunnel?> GetAdminOnboardingAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<AdminOnboardingFunnel>("api/admin/onboarding", cancellationToken);
}
