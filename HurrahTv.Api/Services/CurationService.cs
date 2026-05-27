using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using HurrahTv.Shared.Curation;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public partial class CurationService
{
    private const decimal InputCostPerToken = 0.0000008m;  // haiku: $0.80/MTok in
    private const decimal OutputCostPerToken = 0.000004m;  // haiku: $4.00/MTok out

    // bound concurrent paid AI inferences so a burst can't overshoot the monthly
    // budget by more than `(MatchSlots + CurationSlots) * per-call-cost`. The pre-AI
    // budget check (_db.GetMonthlyAICostAsync) races with detached AIUsage writes
    // under load, so the gates' purpose isn't perfect accounting — they cap the
    // worst case at a known constant. Process-wide (static) because CurationService
    // is scoped (per-request) and a per-instance semaphore would do nothing.
    //
    // two separate gates so a burst of /rows (slow, large token budget) doesn't
    // starve Details-page /match calls (fast, small token budget) behind the same
    // queue. Sized for Haiku throughput at a $50/mo cap: at ~$0.06/curation * 2 +
    // ~$0.005/match * 2 ≈ $0.13 worst-case overshoot, which is the bounded-
    // eventual-consistency option from #124.
    // regenerate the reservoir when it's older than this even if the watchlist is unchanged,
    // so the rotating hero gets genuinely fresh material month-over-month and isn't frozen
    // until the user next touches their list. tune against AIUsage spend. pins #135.
    private const int ReservoirMaxAgeDays = 7;

    private const int MaxConcurrentMatchInferences = 2;
    private const int MaxConcurrentCurationInferences = 2;
    private static readonly SemaphoreSlim AiMatchGate = new(MaxConcurrentMatchInferences, MaxConcurrentMatchInferences);
    private static readonly SemaphoreSlim AiCurationGate = new(MaxConcurrentCurationInferences, MaxConcurrentCurationInferences);

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly DbService _db;
    private readonly TmdbService _tmdb;
    private readonly ILogger<CurationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AIUsageDrainHostedService _drain;
    private readonly string _model;
    private readonly decimal _monthlyBudget;
    private readonly AnthropicClient? _client;

    public bool IsEnabled => _client != null;

    public CurationService(IConfiguration config, DbService db, TmdbService tmdb, ILogger<CurationService> logger, IServiceScopeFactory scopeFactory, AIUsageDrainHostedService drain)
    {
        _db = db;
        _tmdb = tmdb;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _drain = drain;
        _model = config["AI:CurationModel"] ?? "claude-haiku-4-5-20251001";
        _monthlyBudget = config.GetValue<decimal>("AI:MonthlyBudgetUsd", 50m);

        bool enabled = config.GetValue<bool>("AI:Enabled");
        string apiKey = config["AI:AnthropicApiKey"] ?? "";
        _client = enabled && !string.IsNullOrEmpty(apiKey) ? new AnthropicClient { ApiKey = apiKey } : null;
    }

    // detached AIUsage write — fire-and-forget on the thread pool so the request
    // pipeline can return before the cost row lands. DbService is singleton today,
    // so the fresh scope adds no protection; future-proofing for if DbService
    // becomes scoped. The try covers _scopeFactory.CreateScope() too so an
    // ObjectDisposed during host shutdown surfaces in the log rather than as an
    // unobserved Task fault. pins #121.
    //
    // dispatched through AIUsageDrainHostedService.Run so the in-flight task is
    // registered with the drain BEFORE thread-pool scheduling — closes the SIGTERM
    // race window where a Register-after-Task.Run ordering could let StopAsync
    // snapshot empty between the two calls. Run also bounds the inner write with
    // an 8s timeout so it can't outlive the drain's 10s deadline. pins #123.
    public Task TrackUsageDetachedAsync(string userId, int inputTokens, int outputTokens, decimal cost, string requestType)
    {
        return _drain.Run(async ct =>
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                DbService scopedDb = scope.ServiceProvider.GetRequiredService<DbService>();
                await scopedDb.TrackAIUsageAsync(userId, inputTokens, outputTokens, cost, requestType, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // inner-write CT hit the 8s budget — expected during shutdown drain
                // or under DB contention, not a write failure. Log at Warning so it
                // doesn't trip error-level alerts on deploy-slot swaps.
                _logger.LogWarning("Detached AIUsage write cancelled for {UserId} ({RequestType}) — drain budget exceeded", userId, requestType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Detached AIUsage write failed for {UserId} ({RequestType})", userId, requestType);
            }
        });
    }

    public async Task<CurationResult> GetCuratedRowsAsync(string userId, List<QueueItem> watchlist, List<int> providerIds, bool englishOnly = false, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return new CurationResult { Error = "AI not enabled" };

        string currentHash = ComputeWatchlistHash(watchlist);

        // check cache (unless caller forced a refresh — /refresh endpoint passes
        // forceRefresh=true so the cache check is skipped without first blanking the
        // cache row. Pre-blanking lost the user's row on mid-flight client cancel.)
        (string? rowsJson, string? watchlistHash, DateTime? generatedAt)? cached = forceRefresh
            ? null
            : await _db.GetCurationCacheAsync(userId, cancellationToken);
        if (cached != null && cached.Value.watchlistHash == currentHash && cached.Value.rowsJson != null
            && cached.Value.rowsJson != "[]"
            && cached.Value.generatedAt is { } gen && gen > DateTime.UtcNow.AddDays(-ReservoirMaxAgeDays))
        {
            List<AICuratedRow> cachedRows = JsonSerializer.Deserialize<List<AICuratedRow>>(cached.Value.rowsJson) ?? [];
            if (cachedRows.Count > 0)
                return new CurationResult { Rows = cachedRows, FromCache = true, WatchlistChanged = false };
        }

        bool watchlistChanged = cached != null && cached.Value.watchlistHash != currentHash;

        // budget check
        decimal monthlyCost = await _db.GetMonthlyAICostAsync(cancellationToken);
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

        // phase 1: gather candidate pool from TMDb (excluding watchlist)
        List<SearchResult> candidatePool = await GatherCandidatePoolAsync(userId, providerIds, watchlist, englishOnly, cancellationToken);

        if (candidatePool.Count < 10)
            return new CurationResult { Error = "Not enough content available right now — try adding more streaming services", CandidateCount = candidatePool.Count };

        // phase 2: send candidates + taste profile to AI for curation
        List<AICuratedRow> rows = await CurateWithAIAsync(userId, signalItems, watchlist, candidatePool, cancellationToken);

        if (rows.Count == 0)
            return new CurationResult { Error = "Couldn't generate recommendations right now — try again later", CandidateCount = candidatePool.Count };

        string rowsJsonStr = JsonSerializer.Serialize(rows);
        await _db.SetCurationCacheAsync(userId, rowsJsonStr, currentHash, cancellationToken);

        return new CurationResult { Rows = rows, FromCache = false, WatchlistChanged = watchlistChanged, CandidateCount = candidatePool.Count };
    }

    // one rotating hero pick for the Home page. The reservoir (above) is the expensive,
    // periodically-refreshed scored candidate set; selection is a cheap read-time concern
    // that rotates daily and keeps a title out of the hero for a cooldown window. forceRefresh
    // both regenerates the reservoir AND advances past today's already-shown pick. pins #135.
    public async Task<HeroResult> GetCuratedHeroAsync(string userId, List<QueueItem> watchlist, List<int> providerIds,
        bool englishOnly = false, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        CurationResult reservoir = await GetCuratedRowsAsync(userId, watchlist, providerIds, englishOnly, forceRefresh, cancellationToken);
        AICuratedRow? row = reservoir.Rows.FirstOrDefault();
        if (row is null || row.TmdbIds.Count == 0)
            return new HeroResult { Error = reservoir.Error };

        // drop anything added to the watchlist since the reservoir was cached, so a freshly
        // added title can't come back as a recommendation.
        HashSet<int> onWatchlist = [.. watchlist.Select(i => i.TmdbId)];
        List<HeroCandidate> candidates = [.. row.TmdbIds
            .Where(id => !onWatchlist.Contains(id))
            .Select(id => new HeroCandidate(id, row.ItemMediaTypes.GetValueOrDefault(id, MediaTypes.Tv), row.Scores.GetValueOrDefault(id)))];

        Dictionary<int, DateTime> lastShown = await _db.GetHeroImpressionsAsync(userId, cancellationToken);
        HeroCandidate? pick = HeroSelector.Select(candidates, lastShown, DateTime.UtcNow, keepTodaysPickEligible: !forceRefresh);
        if (pick is null)
            return new HeroResult { Error = reservoir.Error };

        await _db.RecordHeroImpressionAsync(userId, pick.TmdbId, cancellationToken);

        return new HeroResult
        {
            TmdbId = pick.TmdbId,
            MediaType = pick.MediaType,
            Reason = row.Reasons.GetValueOrDefault(pick.TmdbId, ""),
            Score = pick.Score
        };
    }

    private async Task<List<SearchResult>> GatherCandidatePoolAsync(string userId, List<int> providerIds, List<QueueItem> watchlist, bool englishOnly, CancellationToken cancellationToken)
    {
        HashSet<int> watchlistIds = [.. watchlist.Select(i => i.TmdbId)];

        // genre-weight the deep-cut band toward the user's stated genre preferences
        // (empty = no genre filter). TMDb treats with_genres as an OR list, so a mix of
        // tv/movie genre ids across both media types is harmless.
        List<int> genreIds = await _db.GetUserGenresAsync(userId, cancellationToken);

        // three bands in parallel across the user's services. The highly-rated back-catalog
        // band (#135) gives the AI non-recent, well-reviewed material so the rotating hero
        // can surface deep cuts, not just the latest hits.
        Task<List<SearchResult>> newTv = _tmdb.NewOnServicesAsync(providerIds, "tv", deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        Task<List<SearchResult>> newMovies = _tmdb.NewOnServicesAsync(providerIds, "movie", deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        Task<List<SearchResult>> popularTv = _tmdb.PopularOnServicesAsync(providerIds, "tv", deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        Task<List<SearchResult>> popularMovies = _tmdb.PopularOnServicesAsync(providerIds, "movie", deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        Task<List<SearchResult>> topTv = _tmdb.HighlyRatedOnServicesAsync(providerIds, "tv", genreIds, deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        Task<List<SearchResult>> topMovies = _tmdb.HighlyRatedOnServicesAsync(providerIds, "movie", genreIds, deep: true, englishOnly: englishOnly, cancellationToken: cancellationToken);
        await Task.WhenAll(newTv, newMovies, popularTv, popularMovies, topTv, topMovies);

        // combine and deduplicate, excluding watchlist items
        HashSet<int> seen = [.. watchlistIds];
        List<SearchResult> pool = [];
        foreach (List<SearchResult> batch in new[] { newTv.Result, newMovies.Result, popularTv.Result, popularMovies.Result, topTv.Result, topMovies.Result })
        {
            foreach (SearchResult r in batch)
            {
                if (seen.Add(r.TmdbId))
                    pool.Add(r);
            }
        }

        // enrich with provider info for shows that don't have it
        pool = await _tmdb.FilterToUserServicesAsync(pool, providerIds, cancellationToken);

        return pool;
    }

    private async Task<List<AICuratedRow>> CurateWithAIAsync(string userId, List<QueueItem> signalItems,
        List<QueueItem> allItems, List<SearchResult> candidates, CancellationToken cancellationToken)
    {
        string tasteProfile = BuildTasteProfile(signalItems, allItems);
        string candidateList = BuildCandidateList(candidates);
        string? firstName = await _db.GetUserFirstNameAsync(userId, cancellationToken);
        string namedAddress = string.IsNullOrWhiteSpace(firstName) ? "" : $"\nThe user's name is {firstName}. Address them by name in 1-2 of the reasons where it lands naturally — don't force it into every line.\n";

        string prompt = $$"""
            You are the AI curator for hurrah.tv. You find surprising connections in what people watch and surface shows they didn't know they'd love.
            {{namedAddress}}
            ## USER'S TASTE PROFILE
            {{tasteProfile}}

            ## CANDIDATE SHOWS (available on their streaming services)
            {{candidateList}}

            ## YOUR TASK
            Pick the 25-30 best shows from the candidates for this user. For each, give:
            - a match score 0-100 — how confident you are THIS user will love it. Be discriminating and use the full range: reserve 90+ for near-certain hits, put genuine stretches in the 50s-60s.
            - a short reason (10-20 words) explaining WHY this user specifically would like it.

            Think beyond genre. Look for:
            - Unexpected connections (they like medical dramas AND comedies → dark comedy about doctors)
            - Mood and pacing matches (if they watch intense dramas, recommend intense dramas)
            - Character archetypes they gravitate toward
            - Non-obvious patterns in their taste

            Rules:
            - Mix eras. Lean toward fresh, recent shows, but include standout older titles when the taste connection is strong — variety matters more than recency.
            - Be opinionated and specific in your reasons. Don't hedge.
            - The list is for adding to their watchlist, not necessarily watching tonight.

            Respond with ONLY a JSON array (no markdown, no explanation):
            [{"id":1,"score":92,"reason":"Why this user specifically would love this show"}]

            The "id" is the candidate number (# before each show). Order best-first by score.
            """;

        await AiCurationGate.WaitAsync(cancellationToken);
        try
        {
            // forward CT so Home-page navigation aborts the paid AI inference. Same
            // pattern as GetShowMatchAsync (#117/#122) — /rows is the most expensive
            // AI path (12-15 picks, larger token budget than /match). pins #126.
            // 25-30 picks, each id+score+reason, needs more headroom than the old 12-15 list
            Message response = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 2048,
                Messages = [new MessageParam { Role = Role.User, Content = prompt }]
            }, cancellationToken: cancellationToken);

            string text = "";
            if (response.Content.Count > 0 && response.Content[0].TryPickText(out TextBlock? textBlock))
                text = textBlock.Text;

            int inputTokens = (int)response.Usage.InputTokens;
            int outputTokens = (int)response.Usage.OutputTokens;
            decimal cost = (inputTokens * InputCostPerToken) + (outputTokens * OutputCostPerToken);

            _ = TrackUsageDetachedAsync(userId, inputTokens, outputTokens, cost, "curation");

            LogCurationUsage(userId, inputTokens, outputTokens, cost);

            // parse response
            text = text.Trim();
            int jsonStart = text.IndexOf('[');
            int jsonEnd = text.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                text = text[jsonStart..(jsonEnd + 1)];

            List<AIPick> picks = JsonSerializer.Deserialize<List<AIPick>>(text, CaseInsensitive) ?? [];

            // build the scored reservoir — a single ranked list the hero rotation draws from
            List<int> tmdbIds = [];
            Dictionary<int, string> reasons = [];
            Dictionary<int, string> itemMediaTypes = [];
            Dictionary<int, int> scores = [];

            foreach (AIPick pick in picks)
            {
                if (pick.Id >= 1 && pick.Id <= candidates.Count)
                {
                    SearchResult candidate = candidates[pick.Id - 1];
                    if (!tmdbIds.Contains(candidate.TmdbId))
                    {
                        tmdbIds.Add(candidate.TmdbId);
                        itemMediaTypes[candidate.TmdbId] = candidate.MediaType;
                        scores[candidate.TmdbId] = Math.Clamp(pick.Score, 0, 100);
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
                Reasons = reasons,
                ItemMediaTypes = itemMediaTypes,
                Scores = scores
            }];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // client navigated away — propagate so endpoint can return 499 instead of
            // burying as a generic "AI curation failed" log. Mirrors GetShowMatchAsync.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI curation failed for {UserId}", userId);
            return [];
        }
        finally
        {
            AiCurationGate.Release();
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
    public async Task<ShowMatchResult?> GetShowMatchAsync(string userId, ShowDetails show, List<QueueItem> watchlist, ShowSentiments? showSentiments = null, QueueItem? queueItem = null, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return null;

        List<QueueItem> signalItems = [.. watchlist.Where(i => i.Status is not QueueStatus.NotForMe)];

        if (signalItems.Count < 2) return null;

        // budget check
        decimal monthlyCost = await _db.GetMonthlyAICostAsync(cancellationToken);
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

        await AiMatchGate.WaitAsync(cancellationToken);
        try
        {
            // forward CT so rapid Details-page navigation aborts the paid AI inference,
            // not just the response on the client side. pins #117.
            Message response = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 150,
                Messages = [new MessageParam { Role = Role.User, Content = prompt }]
            }, cancellationToken: cancellationToken);

            string text = "";
            if (response.Content.Count > 0 && response.Content[0].TryPickText(out TextBlock? textBlock))
                text = textBlock.Text;

            int inputTokens = (int)response.Usage.InputTokens;
            int outputTokens = (int)response.Usage.OutputTokens;
            decimal cost = (inputTokens * InputCostPerToken) + (outputTokens * OutputCostPerToken);

            // fire-and-forget on a fresh DI scope — the request scope may tear down at
            // any moment (RequestAborted or response-sent), and we already paid Anthropic
            // for the inference. detaching guarantees the cost row lands. see #121.
            _ = TrackUsageDetachedAsync(userId, inputTokens, outputTokens, cost, "show-match");

            // parse
            text = text.Trim();
            int jsonStart = text.IndexOf('{');
            int jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                text = text[jsonStart..(jsonEnd + 1)];

            return JsonSerializer.Deserialize<ShowMatchResult>(text, CaseInsensitive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // client navigated away — propagate so endpoint can return 499 instead of
            // pretending success with null.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show match failed for {UserId}/{TmdbId}", userId, show.TmdbId);
            return null;
        }
        finally
        {
            AiMatchGate.Release();
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
        public int Score { get; set; }
        public string Reason { get; set; } = "";
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI curation for {UserId}: {InputTokens} in / {OutputTokens} out = ${Cost:F4}")]
    private partial void LogCurationUsage(string userId, int inputTokens, int outputTokens, decimal cost);
}

// cached row data
public class AICuratedRow
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<int> TmdbIds { get; set; } = [];
    public Dictionary<int, string> Reasons { get; set; } = []; // TmdbId → reason
    public Dictionary<int, string> ItemMediaTypes { get; set; } = [];
    public Dictionary<int, int> Scores { get; set; } = []; // TmdbId → AI match score 0-100 (#135)
}

public class CurationResult
{
    public List<AICuratedRow> Rows { get; set; } = [];
    public bool FromCache { get; set; }
    public bool WatchlistChanged { get; set; }
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
}

// the chosen hero before TMDb hydration — the endpoint resolves TmdbId into a SearchResult.
public class HeroResult
{
    public int? TmdbId { get; set; }
    public string MediaType { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Score { get; set; }
    public string? Error { get; set; }
    public bool HasPick => TmdbId is not null;
}
