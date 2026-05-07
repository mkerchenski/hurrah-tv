using Dapper;
using Npgsql;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class DbService(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required");

    public async Task InitializeAsync()
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS Users (
                Id VARCHAR(50) PRIMARY KEY,
                PhoneNumber VARCHAR(20) NOT NULL UNIQUE,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS OtpCodes (
                Id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                PhoneNumber VARCHAR(20) NOT NULL,
                Code VARCHAR(10) NOT NULL,
                ExpiresAt TIMESTAMPTZ NOT NULL,
                Used BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE INDEX IF NOT EXISTS IX_OtpCodes_Phone ON OtpCodes(PhoneNumber, Used);

            CREATE TABLE IF NOT EXISTS QueueItems (
                Id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                UserId VARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                MediaType VARCHAR(10) NOT NULL,
                Title VARCHAR(500) NOT NULL,
                PosterPath VARCHAR(500) NOT NULL DEFAULT '',
                Position INT NOT NULL DEFAULT 0,
                Status INT NOT NULL DEFAULT 0,
                AvailableOnJson TEXT NOT NULL DEFAULT '[]',
                AddedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS IX_QueueItems_UserId ON QueueItems(UserId);

            CREATE TABLE IF NOT EXISTS UserServices (
                UserId VARCHAR(50) NOT NULL,
                ProviderId INT NOT NULL,
                PRIMARY KEY (UserId, ProviderId)
            );

            CREATE TABLE IF NOT EXISTS UserGenres (
                UserId VARCHAR(50) NOT NULL,
                GenreId INT NOT NULL,
                PRIMARY KEY (UserId, GenreId)
            );

            -- migrate old Paramount+ provider ID (531 → 2303)
            UPDATE UserServices SET ProviderId = 2303 WHERE ProviderId = 531;

            -- soft-hide: preserve history when a user removes a service so re-subscribing restores it
            ALTER TABLE UserServices ADD COLUMN IF NOT EXISTS IsActive BOOLEAN NOT NULL DEFAULT TRUE;

            -- legacy dismissals removed — replaced by sentiment system
            DROP TABLE IF EXISTS UserDismissals;

            -- AI usage tracking per user
            CREATE TABLE IF NOT EXISTS AIUsage (
                Id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                UserId VARCHAR(50) NOT NULL,
                InputTokens INT NOT NULL DEFAULT 0,
                OutputTokens INT NOT NULL DEFAULT 0,
                EstimatedCostUsd DECIMAL(10,6) NOT NULL DEFAULT 0,
                RequestType VARCHAR(50) NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS IX_AIUsage_UserId ON AIUsage(UserId, CreatedAt);

            -- cached AI curation rows per user
            CREATE TABLE IF NOT EXISTS CurationCache (
                UserId VARCHAR(50) PRIMARY KEY,
                RowsJson TEXT NOT NULL,
                WatchlistHash VARCHAR(64) NOT NULL,
                GeneratedAt TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- watchlist evolution: add new columns to QueueItems
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastSeasonWatched INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastEpisodeWatched INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LatestEpisodeDate TIMESTAMPTZ NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS NextEpisodeDate TIMESTAMPTZ NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastEpisodeCheckAt TIMESTAMPTZ NULL;

            CREATE TABLE IF NOT EXISTS UserSettings (
                UserId VARCHAR(50) PRIMARY KEY,
                EnglishOnly BOOLEAN NOT NULL DEFAULT FALSE
            );

            -- sentiment system: add sentiment column, migrate Liked→Finished+favorite, drop rating
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS Sentiment INT NULL;
            UPDATE QueueItems SET Sentiment = 3, Status = 2 WHERE Status = 3 AND Sentiment IS NULL;
            ALTER TABLE QueueItems DROP COLUMN IF EXISTS Rating;

            CREATE TABLE IF NOT EXISTS SeasonSentiments (
                Id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                UserId VARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                SeasonNumber INT NOT NULL,
                Sentiment INT NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(UserId, TmdbId, SeasonNumber)
            );

            CREATE TABLE IF NOT EXISTS EpisodeSentiments (
                Id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                UserId VARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                SeasonNumber INT NOT NULL,
                EpisodeNumber INT NOT NULL,
                Sentiment INT NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(UserId, TmdbId, SeasonNumber, EpisodeNumber)
            );

            -- per-episode watched tracking (does not affect queue status or progress)
            CREATE TABLE IF NOT EXISTS WatchedEpisodes (
                UserId    VARCHAR(50) NOT NULL,
                TmdbId    INT         NOT NULL,
                Season    INT         NOT NULL,
                Episode   INT         NOT NULL,
                WatchedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (UserId, TmdbId, Season, Episode)
            );

            -- specific episode numbers for latest aired and next upcoming
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LatestEpisodeSeason INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LatestEpisodeNumber INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS NextEpisodeSeason   INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS NextEpisodeNumber   INT NULL;

            -- provider refresh: AvailableOnJson is a snapshot at add-time; this tracks when it was last refreshed against TMDb
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS AvailableOnCheckedAt TIMESTAMPTZ NULL;

            -- home page watchlist status filter preferences
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS ShowWatching    BOOL NOT NULL DEFAULT TRUE;
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS ShowWantToWatch BOOL NOT NULL DEFAULT TRUE;
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS ShowFinished    BOOL NOT NULL DEFAULT TRUE;
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS WatchlistSort   VARCHAR(20) NOT NULL DEFAULT 'date';
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS MediaType       VARCHAR(10) NOT NULL DEFAULT 'all';

            -- admin role on users (managed in-app once seeded)
            ALTER TABLE Users ADD COLUMN IF NOT EXISTS IsAdmin BOOLEAN NOT NULL DEFAULT FALSE;

            -- optional first name — collected during onboarding for greetings + AI personalization
            ALTER TABLE Users ADD COLUMN IF NOT EXISTS FirstName VARCHAR(50) NULL;

            -- backdrop image for hero billboard on Home; existing rows default to empty and
            -- will get backfilled via TMDb on the next AddToQueue / EnsureQueueItem touch
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS BackdropPath VARCHAR(500) NOT NULL DEFAULT '';
            """, transaction: tx);

        // bootstrap admins — runs on every startup, idempotent. Owner is always admin in every environment.
        string[] bootstrapPhones = ["4406228711", .. config.GetSection("Admin:BootstrapPhones").Get<string[]>() ?? []];
        await db.ExecuteAsync(
            "UPDATE Users SET IsAdmin = TRUE WHERE PhoneNumber = ANY(@Phones) AND IsAdmin = FALSE",
            new { Phones = bootstrapPhones },
            transaction: tx);

        await tx.CommitAsync();
    }

    // queue operations
    public async Task<List<QueueItem>> GetQueueAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<QueueItem> items = await db.QueryAsync<QueueItem>("""
            SELECT * FROM QueueItems WHERE UserId = @UserId
            ORDER BY
                CASE Status
                    WHEN 1 THEN 0  -- Watching first
                    WHEN 0 THEN 1  -- WantToWatch second
                    WHEN 2 THEN 2  -- Finished third
                    WHEN 4 THEN 3  -- NotForMe last
                END,
                CASE WHEN LatestEpisodeDate >= NOW() - INTERVAL '7 days' THEN 0 ELSE 1 END,
                LatestEpisodeDate DESC,
                Position
            """, new { UserId = userId });
        return [.. items];
    }

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();

        // check for duplicate
        int? existing = await db.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, item.TmdbId, item.MediaType });

        if (existing != null) return null;

        // get next position
        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT COALESCE(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId });

        item.Position = maxPos + 1;

        int id = await db.QuerySingleAsync<int>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, BackdropPath, Position, Status, Sentiment, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @BackdropPath, @Position, @Status, @Sentiment, @AvailableOnJson, @AddedAt)
            RETURNING Id
            """, new
        {
            UserId = userId,
            item.TmdbId,
            item.MediaType,
            item.Title,
            item.PosterPath,
            item.BackdropPath,
            item.Position,
            Status = (int)item.Status,
            item.Sentiment,
            item.AvailableOnJson,
            AddedAt = DateTime.UtcNow
        });

        item.Id = id;
        return item;
    }

    public async Task<bool> RemoveFromQueueAsync(int id, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        int affected = await db.ExecuteAsync(
            "DELETE FROM QueueItems WHERE Id = @Id AND UserId = @UserId",
            new { Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<QueueItem?> UpdateStatusAsync(int id, QueueStatus status, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<QueueItem>(
            "UPDATE QueueItems SET Status = @Status WHERE Id = @Id AND UserId = @UserId RETURNING *",
            new { Status = (int)status, Id = id, UserId = userId });
    }

    public async Task<bool> ReorderAsync(int id, int newPosition, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();

        QueueItem? item = await db.QuerySingleOrDefaultAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE Id = @Id AND UserId = @UserId",
            new { Id = id, UserId = userId }, tx);

        if (item == null) { await tx.RollbackAsync(); return false; }

        int oldPosition = item.Position;
        if (newPosition == oldPosition) { await tx.CommitAsync(); return true; }

        if (newPosition < oldPosition)
        {
            await db.ExecuteAsync(
                "UPDATE QueueItems SET Position = Position + 1 WHERE UserId = @UserId AND Position >= @New AND Position < @Old",
                new { UserId = userId, New = newPosition, Old = oldPosition }, tx);
        }
        else
        {
            await db.ExecuteAsync(
                "UPDATE QueueItems SET Position = Position - 1 WHERE UserId = @UserId AND Position > @Old AND Position <= @New",
                new { UserId = userId, Old = oldPosition, New = newPosition }, tx);
        }

        await db.ExecuteAsync(
            "UPDATE QueueItems SET Position = @Position WHERE Id = @Id",
            new { Position = newPosition, Id = id }, tx);

        await tx.CommitAsync();
        return true;
    }

    public async Task<QueueItem?> UpdateSentimentAsync(int id, int? sentiment, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<QueueItem>(
            "UPDATE QueueItems SET Sentiment = @Sentiment WHERE Id = @Id AND UserId = @UserId RETURNING *",
            new { Sentiment = sentiment, Id = id, UserId = userId });
    }

    public async Task<QueueItem?> UpdateProgressAsync(int id, int? season, int? episode, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<QueueItem>(
            "UPDATE QueueItems SET LastSeasonWatched = @Season, LastEpisodeWatched = @Episode WHERE Id = @Id AND UserId = @UserId RETURNING *",
            new { Season = season, Episode = episode, Id = id, UserId = userId });
    }

    public Task<QueueItem?> MarkAsSeenAsync(int tmdbId, string mediaType, string title, string posterPath, string backdropPath, string availableOnJson, string userId) =>
        UpsertWithStatusAsync(tmdbId, mediaType, title, posterPath, backdropPath, availableOnJson, userId, QueueStatus.Finished,
            existing => existing is QueueStatus.WantToWatch or QueueStatus.Watching);

    // returns the queue item for this (user, tmdbId, mediaType), creating it as WantToWatch
    // if it doesn't exist. never modifies an existing row — use the targeted PUT endpoints
    // (/status, /sentiment) to mutate. used by QuickActions when the dialog opens from a
    // browse-result surface that can't know the item's current queue state.
    public Task<QueueItem?> EnsureQueueItemAsync(int tmdbId, string mediaType, string title, string posterPath, string backdropPath, string availableOnJson, string userId) =>
        UpsertWithStatusAsync(tmdbId, mediaType, title, posterPath, backdropPath, availableOnJson, userId, QueueStatus.WantToWatch,
            existing => false);

    private async Task<QueueItem?> UpsertWithStatusAsync(int tmdbId, string mediaType, string title, string posterPath,
        string backdropPath, string availableOnJson, string userId, QueueStatus targetStatus, Func<QueueStatus, bool>? shouldUpdate = null)
    {
        using NpgsqlConnection db = await OpenAsync();

        QueueItem? existing = await db.QuerySingleOrDefaultAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });

        if (existing != null)
        {
            // backfill backdrop on existing rows that pre-date the column — same row touch as status update
            bool needsBackdropBackfill = string.IsNullOrEmpty(existing.BackdropPath) && !string.IsNullOrEmpty(backdropPath);
            bool willUpdate = shouldUpdate == null || shouldUpdate(existing.Status);
            if (willUpdate && needsBackdropBackfill)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET Status = @Status, BackdropPath = @BackdropPath WHERE Id = @Id",
                    new { Status = (int)targetStatus, BackdropPath = backdropPath, existing.Id });
                existing.Status = targetStatus;
                existing.BackdropPath = backdropPath;
            }
            else if (willUpdate)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET Status = @Status WHERE Id = @Id",
                    new { Status = (int)targetStatus, existing.Id });
                existing.Status = targetStatus;
            }
            else if (needsBackdropBackfill)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET BackdropPath = @BackdropPath WHERE Id = @Id",
                    new { BackdropPath = backdropPath, existing.Id });
                existing.BackdropPath = backdropPath;
            }
            return existing;
        }

        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT COALESCE(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId });

        int id = await db.QuerySingleAsync<int>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, BackdropPath, Position, Status, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @BackdropPath, @Position, @Status, @AvailableOnJson, @AddedAt)
            RETURNING Id
            """, new
        {
            UserId = userId,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            Position = maxPos + 1,
            Status = (int)targetStatus,
            AvailableOnJson = availableOnJson,
            AddedAt = DateTime.UtcNow
        });

        return new QueueItem
        {
            Id = id,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            Position = maxPos + 1,
            Status = targetStatus
        };
    }

    public async Task UpdateProvidersAsync(int id, string availableOnJson, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync();
        CommandDefinition cmd = new("""
            UPDATE QueueItems SET
                AvailableOnJson      = @AvailableOnJson,
                AvailableOnCheckedAt = @CheckedAt
            WHERE Id = @Id
            """, new
        {
            AvailableOnJson = availableOnJson,
            CheckedAt = DateTime.UtcNow,
            Id = id
        }, cancellationToken: cancellationToken);
        await db.ExecuteAsync(cmd);
    }

    public async Task UpdateEpisodeDatesAsync(int id,
        DateTime? latestEpisodeDate, int? latestEpisodeSeason, int? latestEpisodeNumber,
        DateTime? nextEpisodeDate, int? nextEpisodeSeason, int? nextEpisodeNumber,
        CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync();
        CommandDefinition cmd = new("""
            UPDATE QueueItems SET
                LatestEpisodeDate   = @Latest,
                LatestEpisodeSeason = @LatestSeason,
                LatestEpisodeNumber = @LatestNum,
                NextEpisodeDate     = @Next,
                NextEpisodeSeason   = @NextSeason,
                NextEpisodeNumber   = @NextNum,
                LastEpisodeCheckAt  = @CheckedAt
            WHERE Id = @Id
            """, new
        {
            Latest = latestEpisodeDate,
            LatestSeason = latestEpisodeSeason,
            LatestNum = latestEpisodeNumber,
            Next = nextEpisodeDate,
            NextSeason = nextEpisodeSeason,
            NextNum = nextEpisodeNumber,
            CheckedAt = DateTime.UtcNow,
            Id = id
        }, cancellationToken: cancellationToken);
        await db.ExecuteAsync(cmd);
    }

    // user preferences (loads services, genres, and settings in parallel)
    public record UserPreferences(List<int> ProviderIds, List<int> GenreIds, bool EnglishOnly);

    public async Task<UserPreferences> GetUserPreferencesAsync(string userId)
    {
        Task<List<int>> providers = GetUserServicesAsync(userId);
        Task<List<int>> genres = GetUserGenresAsync(userId);
        Task<UserSettings> settings = GetUserSettingsAsync(userId);
        await Task.WhenAll(providers, genres, settings);
        return new UserPreferences(providers.Result, genres.Result, settings.Result.EnglishOnly);
    }

    // user services
    public async Task<List<int>> GetUserServicesAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId AND IsActive = TRUE",
            new { UserId = userId });
        return [.. ids];
    }

    public async Task SetUserServicesAsync(List<int> providerIds, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();

        // flip every existing row inactive, then upsert each id in the new set to active.
        // preserves history so a later re-subscribe restores the user's prior association.
        await db.ExecuteAsync("UPDATE UserServices SET IsActive = FALSE WHERE UserId = @UserId",
            new { UserId = userId }, tx);

        foreach (int pid in providerIds)
        {
            await db.ExecuteAsync("""
                INSERT INTO UserServices (UserId, ProviderId, IsActive)
                VALUES (@UserId, @ProviderId, TRUE)
                ON CONFLICT (UserId, ProviderId) DO UPDATE SET IsActive = TRUE
                """, new { UserId = userId, ProviderId = pid }, tx);
        }

        await tx.CommitAsync();
    }

    // user genres
    public async Task<List<int>> GetUserGenresAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT GenreId FROM UserGenres WHERE UserId = @UserId",
            new { UserId = userId });
        return [.. ids];
    }

    public async Task SetUserGenresAsync(List<int> genreIds, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("DELETE FROM UserGenres WHERE UserId = @UserId", new { UserId = userId }, tx);
        foreach (int gid in genreIds)
        {
            await db.ExecuteAsync("INSERT INTO UserGenres (UserId, GenreId) VALUES (@UserId, @GenreId)",
                new { UserId = userId, GenreId = gid }, tx);
        }

        await tx.CommitAsync();
    }

    // season & episode sentiments
    public async Task<ShowSentiments> GetShowSentimentsAsync(int tmdbId, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<SeasonSentiment> seasons = await db.QueryAsync<SeasonSentiment>(
            "SELECT TmdbId, SeasonNumber, Sentiment FROM SeasonSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId",
            new { UserId = userId, TmdbId = tmdbId });
        IEnumerable<EpisodeSentiment> episodes = await db.QueryAsync<EpisodeSentiment>(
            "SELECT TmdbId, SeasonNumber, EpisodeNumber, Sentiment FROM EpisodeSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId",
            new { UserId = userId, TmdbId = tmdbId });
        return new ShowSentiments { Seasons = [.. seasons], Episodes = [.. episodes] };
    }

    public async Task SetSeasonSentimentAsync(int tmdbId, int seasonNumber, int? sentiment, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        if (sentiment == null)
        {
            await db.ExecuteAsync(
                "DELETE FROM SeasonSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId AND SeasonNumber = @Season",
                new { UserId = userId, TmdbId = tmdbId, Season = seasonNumber });
        }
        else
        {
            await db.ExecuteAsync("""
                INSERT INTO SeasonSentiments (UserId, TmdbId, SeasonNumber, Sentiment)
                VALUES (@UserId, @TmdbId, @Season, @Sentiment)
                ON CONFLICT (UserId, TmdbId, SeasonNumber) DO UPDATE SET Sentiment = @Sentiment
                """, new { UserId = userId, TmdbId = tmdbId, Season = seasonNumber, Sentiment = sentiment });
        }
    }

    public async Task SetEpisodeSentimentAsync(int tmdbId, int seasonNumber, int episodeNumber, int? sentiment, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        if (sentiment == null)
        {
            await db.ExecuteAsync(
                "DELETE FROM EpisodeSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId AND SeasonNumber = @Season AND EpisodeNumber = @Episode",
                new { UserId = userId, TmdbId = tmdbId, Season = seasonNumber, Episode = episodeNumber });
        }
        else
        {
            await db.ExecuteAsync("""
                INSERT INTO EpisodeSentiments (UserId, TmdbId, SeasonNumber, EpisodeNumber, Sentiment)
                VALUES (@UserId, @TmdbId, @Season, @Episode, @Sentiment)
                ON CONFLICT (UserId, TmdbId, SeasonNumber, EpisodeNumber) DO UPDATE SET Sentiment = @Sentiment
                """, new { UserId = userId, TmdbId = tmdbId, Season = seasonNumber, Episode = episodeNumber, Sentiment = sentiment });
        }
    }

    // auth operations
    public async Task SaveOtpCodeAsync(string phoneNumber, string code)
    {
        using NpgsqlConnection db = await OpenAsync();

        // invalidate previous unused codes for this phone
        await db.ExecuteAsync(
            "UPDATE OtpCodes SET Used = TRUE WHERE PhoneNumber = @PhoneNumber AND Used = FALSE",
            new { PhoneNumber = phoneNumber });

        await db.ExecuteAsync(
            "INSERT INTO OtpCodes (PhoneNumber, Code, ExpiresAt) VALUES (@PhoneNumber, @Code, @ExpiresAt)",
            new { PhoneNumber = phoneNumber, Code = code, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
    }

    public async Task<bool> VerifyOtpCodeAsync(string phoneNumber, string code)
    {
        using NpgsqlConnection db = await OpenAsync();

        int? id = await db.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM OtpCodes WHERE PhoneNumber = @PhoneNumber AND Code = @Code AND Used = FALSE AND ExpiresAt > @Now",
            new { PhoneNumber = phoneNumber, Code = code, Now = DateTime.UtcNow });

        if (id == null) return false;

        await db.ExecuteAsync("UPDATE OtpCodes SET Used = TRUE WHERE Id = @Id", new { Id = id });
        return true;
    }

    public async Task<string> GetOrCreateUserAsync(string phoneNumber)
    {
        using NpgsqlConnection db = await OpenAsync();

        string? userId = await db.QuerySingleOrDefaultAsync<string?>(
            "SELECT Id FROM Users WHERE PhoneNumber = @PhoneNumber",
            new { PhoneNumber = phoneNumber });

        if (userId != null) return userId;

        userId = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync(
            "INSERT INTO Users (Id, PhoneNumber, CreatedAt) VALUES (@Id, @PhoneNumber, @CreatedAt)",
            new { Id = userId, PhoneNumber = phoneNumber, CreatedAt = DateTime.UtcNow });

        return userId;
    }

    // ai usage tracking
    public async Task TrackAIUsageAsync(string userId, int inputTokens, int outputTokens, decimal costUsd, string requestType)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO AIUsage (UserId, InputTokens, OutputTokens, EstimatedCostUsd, RequestType, CreatedAt)
            VALUES (@UserId, @InputTokens, @OutputTokens, @CostUsd, @RequestType, @CreatedAt)
            """, new { UserId = userId, InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = costUsd, RequestType = requestType, CreatedAt = DateTime.UtcNow });
    }

    public async Task<decimal> GetMonthlyAICostAsync()
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<decimal>(
            "SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM AIUsage WHERE EXTRACT(YEAR FROM CreatedAt) = @Year AND EXTRACT(MONTH FROM CreatedAt) = @Month",
            new { DateTime.UtcNow.Year, DateTime.UtcNow.Month });
    }

    public async Task<decimal> GetUserAICostAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<decimal>(
            "SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM AIUsage WHERE UserId = @UserId",
            new { UserId = userId });
    }

    // curation cache
    public async Task<(string? rowsJson, string? watchlistHash)?> GetCurationCacheAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        (string RowsJson, string WatchlistHash)? row = await db.QuerySingleOrDefaultAsync<(string RowsJson, string WatchlistHash)?>(
            "SELECT RowsJson, WatchlistHash FROM CurationCache WHERE UserId = @UserId",
            new { UserId = userId });
        return row;
    }

    public async Task SetCurationCacheAsync(string userId, string rowsJson, string watchlistHash)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO CurationCache (UserId, RowsJson, WatchlistHash, GeneratedAt)
            VALUES (@UserId, @RowsJson, @Hash, NOW())
            ON CONFLICT (UserId) DO UPDATE SET RowsJson = @RowsJson, WatchlistHash = @Hash, GeneratedAt = NOW()
            """, new { UserId = userId, RowsJson = rowsJson, Hash = watchlistHash });
    }

    // user settings
    public async Task<UserSettings> GetUserSettingsAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        UserSettings? settings = await db.QuerySingleOrDefaultAsync<UserSettings>(
            "SELECT EnglishOnly, ShowWatching, ShowWantToWatch, ShowFinished, WatchlistSort, MediaType FROM UserSettings WHERE UserId = @UserId",
            new { UserId = userId });
        return settings ?? new UserSettings();
    }

    public async Task SaveUserSettingsAsync(string userId, UserSettings settings)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO UserSettings (UserId, EnglishOnly, ShowWatching, ShowWantToWatch, ShowFinished, WatchlistSort, MediaType)
            VALUES (@UserId, @EnglishOnly, @ShowWatching, @ShowWantToWatch, @ShowFinished, @WatchlistSort, @MediaType)
            ON CONFLICT (UserId) DO UPDATE SET
                EnglishOnly     = @EnglishOnly,
                ShowWatching    = @ShowWatching,
                ShowWantToWatch = @ShowWantToWatch,
                ShowFinished    = @ShowFinished,
                WatchlistSort   = @WatchlistSort,
                MediaType       = @MediaType
            """, new
        {
            UserId = userId,
            settings.EnglishOnly,
            settings.ShowWatching,
            settings.ShowWantToWatch,
            settings.ShowFinished,
            settings.WatchlistSort,
            settings.MediaType
        });
    }

    // watched episodes
    public async Task MarkEpisodeWatchedAsync(string userId, int tmdbId, int season, int episode)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO WatchedEpisodes (UserId, TmdbId, Season, Episode, WatchedAt)
            VALUES (@UserId, @TmdbId, @Season, @Episode, NOW())
            ON CONFLICT DO NOTHING
            """, new { UserId = userId, TmdbId = tmdbId, Season = season, Episode = episode });
    }

    public async Task UnmarkEpisodeWatchedAsync(string userId, int tmdbId, int season, int episode)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync(
            "DELETE FROM WatchedEpisodes WHERE UserId = @UserId AND TmdbId = @TmdbId AND Season = @Season AND Episode = @Episode",
            new { UserId = userId, TmdbId = tmdbId, Season = season, Episode = episode });
    }

    public async Task<List<WatchedEpisode>> GetWatchedEpisodesAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<(int TmdbId, int Season, int Episode)> rows = await db.QueryAsync<(int, int, int)>(
            "SELECT TmdbId, Season, Episode FROM WatchedEpisodes WHERE UserId = @UserId",
            new { UserId = userId });
        return [.. rows.Select(r => new WatchedEpisode(r.TmdbId, r.Season, r.Episode))];
    }

    public async Task<List<WatchedEpisode>> GetWatchedEpisodesForShowAsync(string userId, int tmdbId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<(int TmdbId, int Season, int Episode)> rows = await db.QueryAsync<(int, int, int)>(
            "SELECT TmdbId, Season, Episode FROM WatchedEpisodes WHERE UserId = @UserId AND TmdbId = @TmdbId",
            new { UserId = userId, TmdbId = tmdbId });
        return [.. rows.Select(r => new WatchedEpisode(r.TmdbId, r.Season, r.Episode))];
    }

    // hard delete — wipes the user and every row keyed off their UserId in a single
    // transaction. Issue #22 originally said "no destructive operations from the UI";
    // this is the deliberate exception, gated behind the Admin policy + UI confirmation.
    // Returns true if a Users row was deleted, false if no such user existed.
    public async Task<bool> DeleteUserAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();

        string? phone = await db.QuerySingleOrDefaultAsync<string?>(
            "SELECT PhoneNumber FROM Users WHERE Id = @UserId",
            new { UserId = userId }, transaction: tx);

        if (phone is null)
        {
            await tx.RollbackAsync();
            return false;
        }

        await db.ExecuteAsync("""
            DELETE FROM QueueItems        WHERE UserId = @UserId;
            DELETE FROM UserServices      WHERE UserId = @UserId;
            DELETE FROM UserGenres        WHERE UserId = @UserId;
            DELETE FROM UserSettings      WHERE UserId = @UserId;
            DELETE FROM SeasonSentiments  WHERE UserId = @UserId;
            DELETE FROM EpisodeSentiments WHERE UserId = @UserId;
            DELETE FROM WatchedEpisodes   WHERE UserId = @UserId;
            DELETE FROM AIUsage           WHERE UserId = @UserId;
            DELETE FROM CurationCache     WHERE UserId = @UserId;
            DELETE FROM OtpCodes          WHERE PhoneNumber = @Phone;
            DELETE FROM Users             WHERE Id = @UserId;
            """, new { UserId = userId, Phone = phone }, transaction: tx);

        await tx.CommitAsync();
        return true;
    }

    // user profile
    public async Task<string?> GetUserFirstNameAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<string?>(
            "SELECT FirstName FROM Users WHERE Id = @UserId",
            new { UserId = userId });
    }

    public async Task SetUserFirstNameAsync(string userId, string? firstName)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync(
            "UPDATE Users SET FirstName = @FirstName WHERE Id = @UserId",
            new { UserId = userId, FirstName = firstName });
    }

    // admin role
    public async Task<bool> IsUserAdminAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT IsAdmin FROM Users WHERE Id = @UserId",
            new { UserId = userId });
    }

    public async Task<int> GetAdminCountAsync()
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleAsync<int>("SELECT COUNT(*)::int FROM Users WHERE IsAdmin = TRUE");
    }

    public async Task<bool> SetUserAdminAsync(string userId, bool isAdmin)
    {
        using NpgsqlConnection db = await OpenAsync();
        int rows = await db.ExecuteAsync(
            "UPDATE Users SET IsAdmin = @IsAdmin WHERE Id = @UserId",
            new { UserId = userId, IsAdmin = isAdmin });
        return rows > 0;
    }

    // admin queries — read-only aggregates over all users
    public async Task<List<AdminUserSummary>> GetAdminUserSummariesAsync()
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<AdminUserSummary> rows = await db.QueryAsync<AdminUserSummary>("""
            SELECT
                u.Id                                                            AS Id,
                u.PhoneNumber                                                   AS PhoneNumber,
                u.FirstName                                                     AS FirstName,
                u.CreatedAt                                                     AS CreatedAt,
                u.IsAdmin                                                       AS IsAdmin,
                COALESCE(q.QueueCount, 0)                                       AS QueueCount,
                COALESCE(s.ActiveServiceCount, 0)                               AS ActiveServiceCount,
                COALESCE(a.TotalAiCostUsd, 0)                                   AS TotalAiCostUsd,
                q.LastQueueAddAt                                                AS LastQueueAddAt
            FROM Users u
            LEFT JOIN (
                SELECT UserId, COUNT(*)::int AS QueueCount, MAX(AddedAt) AS LastQueueAddAt
                FROM QueueItems GROUP BY UserId
            ) q ON q.UserId = u.Id
            LEFT JOIN (
                SELECT UserId, COUNT(*)::int AS ActiveServiceCount
                FROM UserServices WHERE IsActive = TRUE GROUP BY UserId
            ) s ON s.UserId = u.Id
            LEFT JOIN (
                SELECT UserId, SUM(EstimatedCostUsd) AS TotalAiCostUsd
                FROM AIUsage GROUP BY UserId
            ) a ON a.UserId = u.Id
            ORDER BY u.CreatedAt DESC
            """);
        return [.. rows];
    }

    public async Task<AdminUserDetail?> GetAdminUserDetailAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();

        (string Id, string PhoneNumber, string? FirstName, DateTime CreatedAt, bool IsAdmin)? user = await db.QuerySingleOrDefaultAsync<(string, string, string?, DateTime, bool)?>(
            "SELECT Id, PhoneNumber, FirstName, CreatedAt, IsAdmin FROM Users WHERE Id = @UserId",
            new { UserId = userId });

        if (user is null) return null;

        IEnumerable<AdminQueueRow> queue = await db.QueryAsync<AdminQueueRow>("""
            SELECT Id, TmdbId, MediaType, Title, Status, Sentiment, AddedAt
            FROM QueueItems WHERE UserId = @UserId
            ORDER BY AddedAt DESC
            """, new { UserId = userId });

        IEnumerable<int> services = await db.QueryAsync<int>(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId AND IsActive = TRUE ORDER BY ProviderId",
            new { UserId = userId });

        IEnumerable<int> genres = await db.QueryAsync<int>(
            "SELECT GenreId FROM UserGenres WHERE UserId = @UserId ORDER BY GenreId",
            new { UserId = userId });

        int seasonSent = await db.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM SeasonSentiments WHERE UserId = @UserId", new { UserId = userId });
        int episodeSent = await db.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM EpisodeSentiments WHERE UserId = @UserId", new { UserId = userId });
        int watched = await db.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM WatchedEpisodes WHERE UserId = @UserId", new { UserId = userId });

        (decimal Cost, int RequestCount) ai = await db.QuerySingleAsync<(decimal Cost, int RequestCount)>(
            "SELECT COALESCE(SUM(EstimatedCostUsd), 0)::decimal AS Cost, COUNT(*)::int AS RequestCount FROM AIUsage WHERE UserId = @UserId",
            new { UserId = userId });

        return new AdminUserDetail(
            user.Value.Id,
            user.Value.PhoneNumber,
            user.Value.FirstName,
            user.Value.CreatedAt,
            user.Value.IsAdmin,
            [.. queue],
            [.. services],
            [.. genres],
            seasonSent,
            episodeSent,
            watched,
            ai.Cost,
            ai.RequestCount);
    }

    public async Task<AdminAiUsageResponse> GetAdminAiUsageAsync()
    {
        using NpgsqlConnection db = await OpenAsync();

        decimal monthToDate = await db.QuerySingleAsync<decimal>("""
            SELECT COALESCE(SUM(EstimatedCostUsd), 0)
            FROM AIUsage
            WHERE date_trunc('month', CreatedAt) = date_trunc('month', NOW())
            """);

        IEnumerable<AdminAiUsageBucket> daily = await db.QueryAsync<AdminAiUsageBucket>("""
            SELECT date_trunc('day', CreatedAt)         AS BucketStart,
                   COALESCE(SUM(InputTokens), 0)::int   AS InputTokens,
                   COALESCE(SUM(OutputTokens), 0)::int  AS OutputTokens,
                   COALESCE(SUM(EstimatedCostUsd), 0)   AS CostUsd,
                   COUNT(*)::int                        AS RequestCount,
                   COUNT(DISTINCT UserId)::int          AS DistinctUsers
            FROM AIUsage
            WHERE CreatedAt >= NOW() - INTERVAL '30 days'
            GROUP BY 1 ORDER BY 1 DESC
            """);

        IEnumerable<AdminAiUsageBucket> weekly = await db.QueryAsync<AdminAiUsageBucket>("""
            SELECT date_trunc('week', CreatedAt)        AS BucketStart,
                   COALESCE(SUM(InputTokens), 0)::int   AS InputTokens,
                   COALESCE(SUM(OutputTokens), 0)::int  AS OutputTokens,
                   COALESCE(SUM(EstimatedCostUsd), 0)   AS CostUsd,
                   COUNT(*)::int                        AS RequestCount,
                   COUNT(DISTINCT UserId)::int          AS DistinctUsers
            FROM AIUsage
            WHERE CreatedAt >= NOW() - INTERVAL '12 weeks'
            GROUP BY 1 ORDER BY 1 DESC
            """);

        IEnumerable<AdminAiUsageBucket> monthly = await db.QueryAsync<AdminAiUsageBucket>("""
            SELECT date_trunc('month', CreatedAt)       AS BucketStart,
                   COALESCE(SUM(InputTokens), 0)::int   AS InputTokens,
                   COALESCE(SUM(OutputTokens), 0)::int  AS OutputTokens,
                   COALESCE(SUM(EstimatedCostUsd), 0)   AS CostUsd,
                   COUNT(*)::int                        AS RequestCount,
                   COUNT(DISTINCT UserId)::int          AS DistinctUsers
            FROM AIUsage
            WHERE CreatedAt >= NOW() - INTERVAL '12 months'
            GROUP BY 1 ORDER BY 1 DESC
            """);

        IEnumerable<AdminAiUsageByType> byType = await db.QueryAsync<AdminAiUsageByType>("""
            SELECT RequestType, COUNT(*)::int AS RequestCount, COALESCE(SUM(EstimatedCostUsd), 0) AS CostUsd
            FROM AIUsage GROUP BY RequestType ORDER BY CostUsd DESC
            """);

        decimal monthlyBudget = decimal.TryParse(config["AI:MonthlyBudgetUsd"], out decimal b) ? b : 0m;

        return new AdminAiUsageResponse(monthToDate, monthlyBudget, [.. daily], [.. weekly], [.. monthly], [.. byType]);
    }

    public async Task<AdminOnboardingFunnel> GetAdminOnboardingFunnelAsync()
    {
        using NpgsqlConnection db = await OpenAsync();

        int total = await db.QuerySingleAsync<int>("SELECT COUNT(*)::int FROM Users");
        int withServices = await db.QuerySingleAsync<int>(
            "SELECT COUNT(DISTINCT UserId)::int FROM UserServices WHERE IsActive = TRUE");
        int withGenres = await db.QuerySingleAsync<int>("SELECT COUNT(DISTINCT UserId)::int FROM UserGenres");
        int withQueue = await db.QuerySingleAsync<int>("SELECT COUNT(DISTINCT UserId)::int FROM QueueItems");

        IEnumerable<AdminSignupBucket> buckets = await db.QueryAsync<AdminSignupBucket>("""
            SELECT date_trunc('day', CreatedAt) AS Day, COUNT(*)::int AS Count
            FROM Users
            WHERE CreatedAt >= NOW() - INTERVAL '60 days'
            GROUP BY 1 ORDER BY 1 DESC
            """);

        return new AdminOnboardingFunnel(total, withServices, withGenres, withQueue, [.. buckets]);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
