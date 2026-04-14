using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class CurationService
{
    private const decimal InputCostPerToken = 0.0000008m;  // haiku: $0.80/MTok in
    private const decimal OutputCostPerToken = 0.000004m;  // haiku: $4.00/MTok out

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly DbService _db;
    private readonly TmdbService _tmdb;
    private readonly ILogger<CurationService> _logger;
    private readonly string _model;
    private readonly decimal _monthlyBudget;
    private readonly AnthropicClient? _client;

    public bool IsEnabled => _client != null;

    public CurationService(IConfiguration config, DbService db, TmdbService tmdb, ILogger<CurationService> logger)
    {
        _db = db;
        _tmdb = tmdb;
        _logger = logger;
        _model = config["AI:CurationModel"] ?? "claude-haiku-4-5-20251001";
        _monthlyBudget = config.GetValue<decimal>("AI:MonthlyBudgetUsd", 50m);

        bool enabled = config.GetValue<bool>("AI:Enabled");
        string apiKey = config["AI:AnthropicApiKey"] ?? "";
        _client = enabled && !string.IsNullOrEmpty(apiKey) ? new AnthropicClient { APIKey = apiKey } : null;
    }

    public async Task<CurationResult> GetCuratedRowsAsync(string userId, List<QueueItem> watchlist, List<int> providerIds, bool englishOnly = false)
    {
        if (!IsEnabled)
            return new CurationResult { Error = "AI not enabled" };

        string currentHash = ComputeWatchlistHash(watchlist);

        // check cache
        (string? rowsJson, string? watchlistHash)? cached = await _db.GetCurationCacheAsync(userId);
        if (cached != null && cached.Value.watchlistHash == currentHash && cached.Value.rowsJson != null
            && cached.Value.rowsJson != "[]")
        {
            List<AICuratedRow> cachedRows = JsonSerializer.Deserialize<List<AICuratedRow>>(cached.Value.rowsJson) ?? [];
            if (cachedRows.Count > 0)
                return new CurationResult { Rows = cachedRows, FromCache = true, WatchlistChanged = false };
        }

        bool watchlistChanged = cached != null && cached.Value.watchlistHash != currentHash;

        // budget check
        decimal monthlyCost = await _db.GetMonthlyAICostAsync();
        if (monthlyCost >= _monthlyBudget)
        {
            _logger.LogWarning("AI monthly budget exceeded: ${Cost:F2} / ${Budget:F2}", monthlyCost, _monthlyBudget);
            return new CurationResult { Error = "AI recommendations are taking a break — check back soon" };
        }

        // need enough signal
        // any watchlist activity counts as signal — stronger statuses weighted in the prompt
        List<QueueItem> signalItems = [.. watchlist.Where(i => i.Status is not QueueStatus.NotForMe)];

        if (signalItems.Count < 2)
            return new CurationResult { Error = "Add a few more shows to your list to unlock AI recommendations" };

        // phase 1: gather candidate pool from TMDb (excluding watchlist + dismissed)
        HashSet<int> dismissed = await _db.GetDismissalsAsync(userId);
        List<SearchResult> candidatePool = await GatherCandidatePoolAsync(providerIds, watchlist, dismissed, englishOnly);

        if (candidatePool.Count < 10)
            return new CurationResult { Error = "Not enough content available right now — try adding more streaming services", CandidateCount = candidatePool.Count };

        // phase 2: send candidates + taste profile to AI for curation
        List<AICuratedRow> rows = await CurateWithAIAsync(userId, signalItems, watchlist, candidatePool);

        if (rows.Count == 0)
            return new CurationResult { Error = "Couldn't generate recommendations right now — try again later", CandidateCount = candidatePool.Count };

        string rowsJsonStr = JsonSerializer.Serialize(rows);
        await _db.SetCurationCacheAsync(userId, rowsJsonStr, currentHash);

        return new CurationResult { Rows = rows, FromCache = false, WatchlistChanged = watchlistChanged, CandidateCount = candidatePool.Count };
    }

    private async Task<List<SearchResult>> GatherCandidatePoolAsync(List<int> providerIds, List<QueueItem> watchlist, HashSet<int> dismissed, bool englishOnly)
    {
        HashSet<int> watchlistIds = [.. watchlist.Select(i => i.TmdbId)];
        watchlistIds.UnionWith(dismissed); // exclude both watchlist and dismissed items

        // fetch recent TV and movies in parallel across user's services
        Task<List<SearchResult>> newTv = _tmdb.NewOnServicesAsync(providerIds, "tv", deep: true, englishOnly: englishOnly);
        Task<List<SearchResult>> newMovies = _tmdb.NewOnServicesAsync(providerIds, "movie", deep: true, englishOnly: englishOnly);
        Task<List<SearchResult>> popularTv = _tmdb.PopularOnServicesAsync(providerIds, "tv", deep: true, englishOnly: englishOnly);
        Task<List<SearchResult>> popularMovies = _tmdb.PopularOnServicesAsync(providerIds, "movie", deep: true, englishOnly: englishOnly);
        await Task.WhenAll(newTv, newMovies, popularTv, popularMovies);

        // combine and deduplicate, excluding watchlist items
        HashSet<int> seen = [.. watchlistIds];
        List<SearchResult> pool = [];
        foreach (List<SearchResult> batch in new[] { newTv.Result, newMovies.Result, popularTv.Result, popularMovies.Result })
        {
            foreach (SearchResult r in batch)
            {
                if (seen.Add(r.TmdbId))
                    pool.Add(r);
            }
        }

        // enrich with provider info for shows that don't have it
        pool = await _tmdb.FilterToUserServicesAsync(pool, providerIds);

        return pool;
    }

    private async Task<List<AICuratedRow>> CurateWithAIAsync(string userId, List<QueueItem> signalItems,
        List<QueueItem> allItems, List<SearchResult> candidates)
    {
        string tasteProfile = BuildTasteProfile(signalItems, allItems);
        string candidateList = BuildCandidateList(candidates);

        string prompt = $$"""
            You are the AI curator for hurrah.tv. You find surprising connections in what people watch and surface shows they didn't know they'd love.

            ## USER'S TASTE PROFILE
            {{tasteProfile}}

            ## CANDIDATE SHOWS (available on their streaming services)
            {{candidateList}}

            ## YOUR TASK
            Pick the 12-15 best shows from the candidates for this user. Rank them by how strongly you'd recommend them. For each, write a short reason (10-20 words) explaining WHY this user specifically would like it.

            Think beyond genre. Look for:
            - Unexpected connections (they like medical dramas AND comedies → dark comedy about doctors)
            - Mood and pacing matches (if they watch intense dramas, recommend intense dramas)
            - Character archetypes they gravitate toward
            - Non-obvious patterns in their taste

            Rules:
            - STRONGLY prefer shows from 2024-2026. Legacy shows only if the taste connection is truly compelling.
            - Be opinionated and specific in your reasons. Don't hedge.
            - The list is for adding to their watchlist, not necessarily watching tonight.

            Respond with ONLY a JSON array (no markdown, no explanation):
            [{"id":1,"reason":"Why this user specifically would love this show"}]

            The "id" is the candidate number (# before each show). Order by recommendation strength (best first).
            """;

        try
        {
            Message response = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 1024,
                Messages = [new MessageParam { Role = Role.User, Content = prompt }]
            });

            string text = "";
            if (response.Content.Count > 0 && response.Content[0].TryPickText(out TextBlock? textBlock))
                text = textBlock.Text;

            int inputTokens = (int)response.Usage.InputTokens;
            int outputTokens = (int)response.Usage.OutputTokens;
            decimal cost = (inputTokens * InputCostPerToken) + (outputTokens * OutputCostPerToken);

            await _db.TrackAIUsageAsync(userId, inputTokens, outputTokens, cost, "curation");

            _logger.LogInformation("AI curation for {UserId}: {InputTokens} in / {OutputTokens} out = ${Cost:F4}",
                userId, inputTokens, outputTokens, cost);

            // parse response
            text = text.Trim();
            int jsonStart = text.IndexOf('[');
            int jsonEnd = text.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                text = text[jsonStart..(jsonEnd + 1)];

            List<AIPick> picks = JsonSerializer.Deserialize<List<AIPick>>(text, CaseInsensitive) ?? [];

            // build a single flat curated list
            List<int> tmdbIds = [];
            Dictionary<int, string> reasons = [];

            foreach (AIPick pick in picks)
            {
                if (pick.Id >= 1 && pick.Id <= candidates.Count)
                {
                    SearchResult candidate = candidates[pick.Id - 1];
                    if (!tmdbIds.Contains(candidate.TmdbId))
                    {
                        tmdbIds.Add(candidate.TmdbId);
                        if (!string.IsNullOrEmpty(pick.Reason))
                            reasons[candidate.TmdbId] = pick.Reason;
                    }
                }
            }

            if (tmdbIds.Count == 0) return [];

            return [new AICuratedRow
            {
                Title = "Curated for You",
                Subtitle = "Personalized picks based on your taste",
                TmdbIds = tmdbIds,
                Reasons = reasons
            }];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI curation failed for {UserId}", userId);
            return [];
        }
    }

    private static string BuildTasteProfile(List<QueueItem> signalItems, List<QueueItem> allItems)
    {
        StringBuilder sb = new();

        // sentiment-based grouping (strongest signal)
        List<QueueItem> favorites = [.. signalItems.Where(i => i.Sentiment == SentimentLevel.Favorite)];
        List<QueueItem> thumbsUp = [.. signalItems.Where(i => i.Sentiment == SentimentLevel.Up)];
        List<QueueItem> thumbsDown = [.. allItems.Where(i => i.Sentiment == SentimentLevel.Down)];

        // list-based grouping (weaker signal when no sentiment)
        List<QueueItem> watched = [.. signalItems.Where(i => i.Status == QueueStatus.Finished && i.Sentiment == null)];
        List<QueueItem> watching = [.. signalItems.Where(i => i.Status == QueueStatus.Watching && i.Sentiment == null)];
        List<QueueItem> wantToWatch = [.. allItems.Where(i => i.Status == QueueStatus.WantToWatch && i.Sentiment == null)];
        List<QueueItem> notForMe = [.. allItems.Where(i => i.Status == QueueStatus.NotForMe && i.Sentiment == null)];

        // sentiment signals first (strongest)
        if (favorites.Count > 0)
            sb.AppendLine($"FAVORITES (strongest signal): {string.Join(", ", favorites.Select(i => $"{i.Title} ({i.MediaType})"))}");
        if (thumbsUp.Count > 0)
            sb.AppendLine($"LIKED (strong signal): {string.Join(", ", thumbsUp.Select(i => $"{i.Title} ({i.MediaType})"))}");
        if (thumbsDown.Count > 0)
            sb.AppendLine($"DISLIKED (avoid similar): {string.Join(", ", thumbsDown.Take(10).Select(i => i.Title))}");

        // list-only signals (no sentiment set)
        if (watched.Count > 0)
            sb.AppendLine($"WATCHED (no sentiment — moderate signal): {string.Join(", ", watched.Select(i => $"{i.Title} ({i.MediaType})"))}");
        if (watching.Count > 0)
            sb.AppendLine($"CURRENTLY WATCHING (interest signal): {string.Join(", ", watching.Select(i => $"{i.Title} ({i.MediaType})"))}");
        if (wantToWatch.Count > 0)
            sb.AppendLine($"WANT TO WATCH (weaker signal): {string.Join(", ", wantToWatch.Take(10).Select(i => $"{i.Title} ({i.MediaType})"))}");
        if (notForMe.Count > 0)
            sb.AppendLine($"NOT FOR ME (avoid): {string.Join(", ", notForMe.Take(10).Select(i => i.Title))}");

        sb.AppendLine();
        sb.AppendLine("Weight recommendations heavily toward FAVORITES and LIKED shows. Items with no sentiment use their list status as a weaker signal.");

        return sb.ToString();
    }

    private static string BuildCandidateList(List<SearchResult> candidates)
    {
        StringBuilder sb = new();
        for (int i = 0; i < candidates.Count; i++)
        {
            SearchResult c = candidates[i];
            string services = c.AvailableOn.Count > 0
                ? string.Join("/", c.AvailableOn.Take(3).Select(s => s.ProviderName))
                : "";
            string overview = c.Overview.Length > 100 ? c.Overview[..100] + "..." : c.Overview;
            sb.AppendLine($"#{i + 1} {c.Title} ({c.MediaType.ToUpper()}, {c.Year}) [{services}] — {overview}");
        }
        return sb.ToString();
    }

    // personalized match blurb for a specific show
    public async Task<ShowMatchResult?> GetShowMatchAsync(string userId, ShowDetails show, List<QueueItem> watchlist, ShowSentiments? showSentiments = null, QueueItem? queueItem = null)
    {
        if (!IsEnabled) return null;

        List<QueueItem> signalItems = [.. watchlist.Where(i => i.Status is not QueueStatus.NotForMe)];

        if (signalItems.Count < 2) return null;

        // budget check
        decimal monthlyCost = await _db.GetMonthlyAICostAsync();
        if (monthlyCost >= _monthlyBudget) return null;

        string tasteProfile = BuildTasteProfile(signalItems, watchlist);

        string genres = show.Genres.Count > 0 ? string.Join(", ", show.Genres) : "unknown";
        string overview = show.Overview.Length > 200 ? show.Overview[..200] + "..." : show.Overview;

        // build episode sentiment context if available
        string sentimentContext = "";
        if (showSentiments != null && (showSentiments.Seasons.Count > 0 || showSentiments.Episodes.Count > 0))
        {
            StringBuilder sentSb = new();
            sentSb.AppendLine("USER'S SENTIMENT ON THIS SHOW:");
            foreach (SeasonSentiment ss in showSentiments.Seasons.OrderBy(s => s.SeasonNumber))
                sentSb.AppendLine($"  Season {ss.SeasonNumber}: {SentimentLabel(ss.Sentiment)}");
            if (showSentiments.Episodes.Count > 0)
            {
                IOrderedEnumerable<IGrouping<int, EpisodeSentiment>> bySeason = showSentiments.Episodes.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key);
                foreach (IGrouping<int, EpisodeSentiment> group in bySeason)
                {
                    int ups = group.Count(e => e.Sentiment == SentimentLevel.Up);
                    int favs = group.Count(e => e.Sentiment == SentimentLevel.Favorite);
                    int downs = group.Count(e => e.Sentiment == SentimentLevel.Down);
                    sentSb.AppendLine($"  Season {group.Key} episodes: {ups} liked, {favs} loved, {downs} disliked");
                }
            }
            sentimentContext = sentSb.ToString();
        }

        // adapt the prompt based on the user's relationship with this show
        string roleInstruction = BuildRoleInstruction(queueItem);

        string prompt = $$"""
            You are the AI curator for hurrah.tv. {{roleInstruction}}

            USER'S TASTE:
            {{tasteProfile}}
            {{sentimentContext}}
            SHOW: {{show.Title}} ({{show.MediaType.ToUpper()}}, {{show.Year}})
            Genres: {{genres}}
            Rating: {{show.VoteAverage:F1}}/10
            Overview: {{overview}}

            Write 1-2 sentences MAX. Be direct and specific — reference what in their taste connects to this show. If the user has rated seasons or episodes, reference their experience. Don't hedge.

            Respond with ONLY a JSON object (no markdown):
            {"match":"strong" or "good" or "stretch" or "miss","reason":"Your 1-2 sentence take"}
            """;

        try
        {
            Message response = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 150,
                Messages = [new MessageParam { Role = Role.User, Content = prompt }]
            });

            string text = "";
            if (response.Content.Count > 0 && response.Content[0].TryPickText(out TextBlock? textBlock))
                text = textBlock.Text;

            int inputTokens = (int)response.Usage.InputTokens;
            int outputTokens = (int)response.Usage.OutputTokens;
            decimal cost = (inputTokens * InputCostPerToken) + (outputTokens * OutputCostPerToken);

            await _db.TrackAIUsageAsync(userId, inputTokens, outputTokens, cost, "show-match");

            // parse
            text = text.Trim();
            int jsonStart = text.IndexOf('{');
            int jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                text = text[jsonStart..(jsonEnd + 1)];

            return JsonSerializer.Deserialize<ShowMatchResult>(text, CaseInsensitive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show match failed for {UserId}/{TmdbId}", userId, show.TmdbId);
            return null;
        }
    }

    private static string BuildRoleInstruction(QueueItem? queueItem)
    {
        if (queueItem == null)
            return "Based on this user's taste profile, write a brief personalized take on whether they'd enjoy this show. If it's a strong match, say so confidently. If it's a stretch, say why it might surprise them. If it's not for them, be honest.";

        string sentimentNote = queueItem.Sentiment switch
        {
            SentimentLevel.Favorite => " They marked it as a favorite.",
            SentimentLevel.Up => " They gave it a thumbs up.",
            SentimentLevel.Down => " They gave it a thumbs down.",
            _ => ""
        };

        return queueItem.Status switch
        {
            QueueStatus.Watching =>
                $"This user is currently watching this show.{sentimentNote} Don't tell them whether they'd like it — they already chose it. Instead, give insight into WHY this show appeals to them based on their taste patterns, or what to look forward to.",
            QueueStatus.Finished when queueItem.Sentiment == SentimentLevel.Down =>
                "This user watched this show and disliked it. Reflect on why it didn't work for them based on their taste profile — what specifically clashed with their preferences.",
            QueueStatus.Finished =>
                $"This user already watched this show.{sentimentNote} Don't ask if they'd enjoy it. Instead, connect it to their broader taste — what does loving (or just finishing) this show reveal about what they're drawn to? Suggest what itch it scratched.",
            QueueStatus.WantToWatch =>
                $"This user has bookmarked this show but hasn't started it yet.{sentimentNote} Build excitement — based on their taste, highlight specifically why this will deliver for them.",
            QueueStatus.NotForMe =>
                "This user dismissed this show. Be honest about why it probably isn't for them based on their taste profile.",
            _ =>
                "Based on this user's taste profile, write a brief personalized take on this show."
        };
    }

    private static string SentimentLabel(int sentiment) => sentiment switch
    {
        SentimentLevel.Down => "disliked",
        SentimentLevel.Up => "liked",
        SentimentLevel.Favorite => "loved",
        _ => "unknown"
    };

    private static string ComputeWatchlistHash(List<QueueItem> items)
    {
        string input = string.Join("|", items
            .OrderBy(i => i.TmdbId)
            .Select(i => $"{i.TmdbId}:{(int)i.Status}:{i.Sentiment}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }

    private class AIPick
    {
        public int Id { get; set; }
        public string Reason { get; set; } = "";
    }
}

// cached row data
public class AICuratedRow
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<int> TmdbIds { get; set; } = [];
    public Dictionary<int, string> Reasons { get; set; } = []; // TmdbId → reason
}

public class CurationResult
{
    public List<AICuratedRow> Rows { get; set; } = [];
    public bool FromCache { get; set; }
    public bool WatchlistChanged { get; set; }
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
}
