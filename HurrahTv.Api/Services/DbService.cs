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

            CREATE TABLE IF NOT EXISTS UserDismissals (
                UserId VARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                DismissedAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (UserId, TmdbId)
            );

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
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS Rating INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastSeasonWatched INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastEpisodeWatched INT NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LatestEpisodeDate TIMESTAMPTZ NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS NextEpisodeDate TIMESTAMPTZ NULL;
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS LastEpisodeCheckAt TIMESTAMPTZ NULL;

            CREATE TABLE IF NOT EXISTS UserSettings (
                UserId VARCHAR(50) PRIMARY KEY,
                EnglishOnly BOOLEAN NOT NULL DEFAULT FALSE
            );
            """, transaction: tx);

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
                    WHEN 3 THEN 2  -- Liked third
                    WHEN 2 THEN 3  -- Finished fourth
                    WHEN 4 THEN 4  -- NotForMe last
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
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, Position, Status, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @Position, @Status, @AvailableOnJson, @AddedAt)
            RETURNING Id
            """, new
        {
            UserId = userId,
            item.TmdbId,
            item.MediaType,
            item.Title,
            item.PosterPath,
            item.Position,
            Status = (int)item.Status,
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

    public async Task<bool> UpdateStatusAsync(int id, QueueStatus status, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        int affected = await db.ExecuteAsync(
            "UPDATE QueueItems SET Status = @Status WHERE Id = @Id AND UserId = @UserId",
            new { Status = (int)status, Id = id, UserId = userId });
        return affected > 0;
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

    public async Task<bool> UpdateRatingAsync(int id, int? rating, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        int affected = await db.ExecuteAsync(
            "UPDATE QueueItems SET Rating = @Rating WHERE Id = @Id AND UserId = @UserId",
            new { Rating = rating, Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> UpdateProgressAsync(int id, int? season, int? episode, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        int affected = await db.ExecuteAsync(
            "UPDATE QueueItems SET LastSeasonWatched = @Season, LastEpisodeWatched = @Episode WHERE Id = @Id AND UserId = @UserId",
            new { Season = season, Episode = episode, Id = id, UserId = userId });
        return affected > 0;
    }

    public Task<QueueItem?> MarkAsSeenAsync(int tmdbId, string mediaType, string title, string posterPath, string availableOnJson, string userId) =>
        UpsertWithStatusAsync(tmdbId, mediaType, title, posterPath, availableOnJson, userId, QueueStatus.Finished,
            existing => existing is QueueStatus.WantToWatch or QueueStatus.Watching);

    public Task<QueueItem?> MarkAsLikedAsync(int tmdbId, string mediaType, string title, string posterPath, string availableOnJson, string userId) =>
        UpsertWithStatusAsync(tmdbId, mediaType, title, posterPath, availableOnJson, userId, QueueStatus.Liked);

    private async Task<QueueItem?> UpsertWithStatusAsync(int tmdbId, string mediaType, string title, string posterPath,
        string availableOnJson, string userId, QueueStatus targetStatus, Func<QueueStatus, bool>? shouldUpdate = null)
    {
        using NpgsqlConnection db = await OpenAsync();

        QueueItem? existing = await db.QuerySingleOrDefaultAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType });

        if (existing != null)
        {
            if (shouldUpdate == null || shouldUpdate(existing.Status))
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET Status = @Status WHERE Id = @Id",
                    new { Status = (int)targetStatus, existing.Id });
                existing.Status = targetStatus;
            }
            return existing;
        }

        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT COALESCE(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId });

        int id = await db.QuerySingleAsync<int>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, Position, Status, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @Position, @Status, @AvailableOnJson, @AddedAt)
            RETURNING Id
            """, new
        {
            UserId = userId,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = title,
            PosterPath = posterPath,
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
            Position = maxPos + 1,
            Status = targetStatus
        };
    }

    public async Task UpdateEpisodeDatesAsync(int id, DateTime? latestEpisodeDate, DateTime? nextEpisodeDate)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            UPDATE QueueItems SET LatestEpisodeDate = @Latest, NextEpisodeDate = @Next, LastEpisodeCheckAt = @CheckedAt
            WHERE Id = @Id
            """, new { Latest = latestEpisodeDate, Next = nextEpisodeDate, CheckedAt = DateTime.UtcNow, Id = id });
    }

    // user preferences (loads services, genres, dismissals, and settings in parallel)
    public record UserPreferences(List<int> ProviderIds, List<int> GenreIds, HashSet<int> Dismissed, bool EnglishOnly);

    public async Task<UserPreferences> GetUserPreferencesAsync(string userId)
    {
        Task<List<int>> providers = GetUserServicesAsync(userId);
        Task<List<int>> genres = GetUserGenresAsync(userId);
        Task<HashSet<int>> dismissed = GetDismissalsAsync(userId);
        Task<UserSettings> settings = GetUserSettingsAsync(userId);
        await Task.WhenAll(providers, genres, dismissed, settings);
        return new UserPreferences(providers.Result, genres.Result, dismissed.Result, settings.Result.EnglishOnly);
    }

    // user services
    public async Task<List<int>> GetUserServicesAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId",
            new { UserId = userId });
        return [.. ids];
    }

    public async Task SetUserServicesAsync(List<int> providerIds, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("DELETE FROM UserServices WHERE UserId = @UserId", new { UserId = userId }, tx);
        foreach (int pid in providerIds)
        {
            await db.ExecuteAsync("INSERT INTO UserServices (UserId, ProviderId) VALUES (@UserId, @ProviderId)",
                new { UserId = userId, ProviderId = pid }, tx);
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

    // dismissals
    public async Task<HashSet<int>> GetDismissalsAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT TmdbId FROM UserDismissals WHERE UserId = @UserId",
            new { UserId = userId });
        return [.. ids];
    }

    public async Task DismissAsync(int tmdbId, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync(
            "INSERT INTO UserDismissals (UserId, TmdbId) VALUES (@UserId, @TmdbId) ON CONFLICT DO NOTHING",
            new { UserId = userId, TmdbId = tmdbId });
    }

    public async Task ClearDismissalsAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("DELETE FROM UserDismissals WHERE UserId = @UserId", new { UserId = userId });
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
            "SELECT EnglishOnly FROM UserSettings WHERE UserId = @UserId",
            new { UserId = userId });
        return settings ?? new UserSettings();
    }

    public async Task SaveUserSettingsAsync(string userId, UserSettings settings)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO UserSettings (UserId, EnglishOnly)
            VALUES (@UserId, @EnglishOnly)
            ON CONFLICT (UserId) DO UPDATE SET EnglishOnly = @EnglishOnly
            """, new { UserId = userId, settings.EnglishOnly });
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
