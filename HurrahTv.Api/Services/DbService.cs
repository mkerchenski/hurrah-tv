using Dapper;
using Npgsql;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class DbService(NpgsqlDataSource dataSource, IConfiguration config)
{
    // rent from a single shared, pool-warmed NpgsqlDataSource (configured in Program.cs) rather
    // than `new NpgsqlConnection(connString)` per call — a Min-Pool-Size=0 pool drained to zero
    // when idle, so the first post-idle request paid a full cross-region cold connect and could
    // exceed the 15s connection timeout, stalling Home's /api/queue (#200, Sentry HURRAH-TV-3).
    private readonly NpgsqlDataSource _dataSource = dataSource;

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

            -- per-user record of when a title was last featured in the Home hero, so the
            -- daily rotation can keep a title out of the hero for a cooldown window (#135).
            -- keyed by (TmdbId, MediaType): TMDb ids are namespaced per media type, so a
            -- movie and a TV show sharing a numeric id must not share a cooldown (#146).
            CREATE TABLE IF NOT EXISTS CurationHeroImpressions (
                UserId VARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                MediaType VARCHAR(10) NOT NULL,
                ShownAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (UserId, TmdbId, MediaType)
            );

            -- one-time migration for pre-#146 databases where the table predates the
            -- MediaType column / composite PK. Impression rows are ephemeral cooldown
            -- state (a 14-day window), so a recreate is safe — at worst a hero repeats
            -- once post-deploy. Guarded on the column so it's a no-op once migrated.
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'curationheroimpressions' AND column_name = 'mediatype'
                ) THEN
                    DROP TABLE CurationHeroImpressions;
                    CREATE TABLE CurationHeroImpressions (
                        UserId VARCHAR(50) NOT NULL,
                        TmdbId INT NOT NULL,
                        MediaType VARCHAR(10) NOT NULL,
                        ShownAt TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                        PRIMARY KEY (UserId, TmdbId, MediaType)
                    );
                END IF;
            END $$;

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

            -- in-app user feedback (#19) — read in the admin view
            CREATE TABLE IF NOT EXISTS Feedback (
                Id           INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                UserId       VARCHAR(50)  NOT NULL,
                Category     VARCHAR(30)  NOT NULL,
                Message      TEXT         NOT NULL,
                ContactEmail VARCHAR(255) NULL,
                CreatedAt    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS IX_Feedback_CreatedAt ON Feedback(CreatedAt DESC);

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

            -- last changelog version the user has seen — drives the new-feature alert banner (#19)
            ALTER TABLE UserSettings ADD COLUMN IF NOT EXISTS LastSeenChangelogVersion VARCHAR(50) NULL;

            -- admin role on users (managed in-app once seeded)
            ALTER TABLE Users ADD COLUMN IF NOT EXISTS IsAdmin BOOLEAN NOT NULL DEFAULT FALSE;

            -- optional first name — collected during onboarding for greetings + AI personalization
            ALTER TABLE Users ADD COLUMN IF NOT EXISTS FirstName VARCHAR(50) NULL;

            -- backdrop image for hero billboard on Home; existing rows default to empty and
            -- will get backfilled via TMDb on the next AddToQueue / EnsureQueueItem touch
            ALTER TABLE QueueItems ADD COLUMN IF NOT EXISTS BackdropPath VARCHAR(500) NOT NULL DEFAULT '';

            -- queue dedup (#155). Collapse any pre-constraint duplicate rows, keeping the
            -- earliest AddedAt (tie-break: lowest Id) per (UserId, TmdbId, MediaType), then
            -- enforce uniqueness at the DB level so races and alternative insert paths can't
            -- land a second row. Cleanup MUST run before the index or index creation fails on
            -- the existing dupes. Both are idempotent — no-ops once the data is clean.
            DELETE FROM QueueItems
            WHERE Id IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (
                        PARTITION BY UserId, TmdbId, MediaType
                        ORDER BY AddedAt ASC, Id ASC
                    ) AS rn
                    FROM QueueItems
                ) ranked
                WHERE rn > 1
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_QueueItems_User_Tmdb_Media
                ON QueueItems(UserId, TmdbId, MediaType);
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
    public async Task<List<QueueItem>> GetQueueAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        // canonical status ordering: see HurrahTv.Shared.Models.QueueStatusOrdering.DisplayOrder
        // — Client UI sorts share the same rule via QueueStatusOrdering.SortPriority.
        // ELSE 4 matches DisplayOrder.Count so unknown enum values sort after every known one.
        //
        // WantToWatch (Status=0) is user-curated via drag-reorder, so its sole secondary
        // sort is Position. Recency-aware ordering (freshness window + LatestEpisodeDate
        // DESC) only applies to non-WantToWatch statuses, where surfacing new episodes is
        // the point. The CASE WHEN Status = 0 / != 0 split makes those keys NULL for the
        // other group so NULLS LAST keeps each status's branch isolated. Pins #101.
        CommandDefinition cmd = new("""
            SELECT * FROM QueueItems WHERE UserId = @UserId
            ORDER BY
                CASE Status
                    WHEN 1 THEN 0  -- Watching first
                    WHEN 0 THEN 1  -- WantToWatch second
                    WHEN 2 THEN 2  -- Finished third
                    WHEN 4 THEN 3  -- NotForMe last
                    ELSE 4         -- unknown sorts last (matches QueueStatusOrdering.SortPriority)
                END,
                CASE WHEN Status = 0 THEN Position END ASC NULLS LAST,
                CASE WHEN Status != 0 AND LatestEpisodeDate >= NOW() - INTERVAL '7 days'
                      AND LatestEpisodeDate <= NOW() THEN 0 ELSE 1 END,
                CASE WHEN Status != 0 THEN LatestEpisodeDate END DESC NULLS LAST,
                Position
            """, new { UserId = userId }, cancellationToken: cancellationToken);
        IEnumerable<QueueItem> items = await db.QueryAsync<QueueItem>(cmd);
        return [.. items];
    }

    // single-row lookup for the Details page (#8) — avoids fetching the whole watchlist just to
    // find one item. Scoped by UserId like every other queue read. (UserId, TmdbId, MediaType) is
    // the dedup key (UNIQUE constraint from #155), so QueryFirstOrDefault returns at most one row.
    public async Task<QueueItem?> GetQueueItemAsync(string userId, int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT * FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType }, cancellationToken: cancellationToken);
        return await db.QueryFirstOrDefaultAsync<QueueItem>(cmd);
    }

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item, string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();

        // serialize concurrent adds for THIS user so two distinct-title adds can't read the
        // same MAX(Position) and collide on Position. xact-scoped — auto-released on commit.
        // keyed per-user (not global) so unrelated users never contend. pins #163.
        await LockUserQueueAsync(db, userId, tx);

        // next position — only consumed when the INSERT actually lands a row
        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT COALESCE(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId }, tx);

        item.Position = maxPos + 1;

        // ON CONFLICT DO NOTHING makes the add idempotent at the DB level (#155) — a race
        // (double-tap, two tabs) or an alternative insert path can't create a second
        // (UserId, TmdbId, MediaType) row. RETURNING yields the new Id only when a row was
        // actually inserted; a conflict returns no row, so we re-read the existing one below.
        int? id = await db.QuerySingleOrDefaultAsync<int?>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, BackdropPath, Position, Status, Sentiment, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @BackdropPath, @Position, @Status, @Sentiment, @AvailableOnJson, @AddedAt)
            ON CONFLICT (UserId, TmdbId, MediaType) DO NOTHING
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
        }, tx);

        if (id is not null)
        {
            await tx.CommitAsync();
            item.Id = id.Value;
            return item;
        }

        // conflict — the title is already queued. Return the existing row so the add is
        // idempotent (the caller surfaces it as success, not a 409). pins #155.
        QueueItem? existing = await SelectQueueItemAsync(db, userId, item.TmdbId, item.MediaType, tx);
        await tx.CommitAsync();
        return existing;
    }

    // per-user transaction-scoped advisory lock. hashtextextended returns a 64-bit key (unlike
    // hashtext's 32-bit value, whose narrow space made cross-user slot collisions plausible at
    // scale), so distinct users effectively never contend (a 64-bit collision is astronomically
    // unlikely). The lock is released automatically when tx commits or rolls back, so callers
    // never have to unlock explicitly. pins #163.
    private static async Task LockUserQueueAsync(NpgsqlConnection db, string userId, NpgsqlTransaction tx) =>
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@UserId, 0))", new { UserId = userId }, tx);

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

        // applies the status / backdrop-backfill policy to a row that already exists. Shared
        // between the fast path (row found by the initial SELECT) and the race-lost path (the
        // INSERT below hit the unique constraint), so both behave identically. pins #155.
        // INVARIANT (#183): every branch sets an absolute value keyed by primary Id and is
        // idempotent — so it's safe to run outside the advisory lock (fast path) or enlisted in the
        // caller's tx (race-lost path). A future read-modify-write or relative-update policy would
        // NOT be safe this way and MUST run inside the tx/lock on both paths. The optional tx must
        // be passed when an open transaction exists on db, or Npgsql throws.
        async Task<QueueItem> ApplyUpdatePolicy(QueueItem row, NpgsqlTransaction? tx = null)
        {
            // backfill backdrop on existing rows that pre-date the column — same row touch as status update
            bool needsBackdropBackfill = string.IsNullOrEmpty(row.BackdropPath) && !string.IsNullOrEmpty(backdropPath);
            bool willUpdate = shouldUpdate == null || shouldUpdate(row.Status);
            if (willUpdate && needsBackdropBackfill)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET Status = @Status, BackdropPath = @BackdropPath WHERE Id = @Id",
                    new { Status = (int)targetStatus, BackdropPath = backdropPath, row.Id }, tx);
                row.Status = targetStatus;
                row.BackdropPath = backdropPath;
            }
            else if (willUpdate)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET Status = @Status WHERE Id = @Id",
                    new { Status = (int)targetStatus, row.Id }, tx);
                row.Status = targetStatus;
            }
            else if (needsBackdropBackfill)
            {
                await db.ExecuteAsync(
                    "UPDATE QueueItems SET BackdropPath = @BackdropPath WHERE Id = @Id",
                    new { BackdropPath = backdropPath, row.Id }, tx);
                row.BackdropPath = backdropPath;
            }
            return row;
        }

        QueueItem? existing = await SelectQueueItemAsync(db, userId, tmdbId, mediaType);

        // fast path — no transaction/lock: the policy only issues single-row "UPDATE ... WHERE Id"
        // statements (atomic in Postgres) and allocates no Position, so there's no MAX-read race to
        // serialize. Safe given the idempotent-by-Id invariant documented on ApplyUpdatePolicy.
        if (existing != null)
            return await ApplyUpdatePolicy(existing);

        // insert branch — serialize per-user so concurrent distinct-title adds can't read the
        // same MAX(Position) and collide. only the insert path takes the lock; the fast path
        // above mutates a single row by Id and never allocates a Position. pins #163.
        using NpgsqlTransaction tx = await db.BeginTransactionAsync();
        await LockUserQueueAsync(db, userId, tx);

        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT COALESCE(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId }, tx);

        // ON CONFLICT DO NOTHING guards against a concurrent caller inserting the same
        // (UserId, TmdbId, MediaType) between our SELECT and INSERT — without it the new
        // unique constraint (#155) would surface that race as a 500.
        int? id = await db.QuerySingleOrDefaultAsync<int?>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, BackdropPath, Position, Status, AvailableOnJson, AddedAt)
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @BackdropPath, @Position, @Status, @AvailableOnJson, @AddedAt)
            ON CONFLICT (UserId, TmdbId, MediaType) DO NOTHING
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
        }, tx);

        if (id is null)
        {
            // lost the insert race — the row exists now. Re-read and apply the same policy
            // the fast path would have, so a concurrent /seen + /ensure can't diverge.
            // Run the policy enlisted in tx, BEFORE commit, so the mutation stays covered by the
            // advisory lock instead of landing after the lock is released (#183).
            existing = await SelectQueueItemAsync(db, userId, tmdbId, mediaType, tx);
            QueueItem? result = existing is null ? null : await ApplyUpdatePolicy(existing, tx);
            await tx.CommitAsync();
            return result;
        }

        await tx.CommitAsync();

        return new QueueItem
        {
            Id = id.Value,
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
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
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
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
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

    public async Task<UserPreferences> GetUserPreferencesAsync(string userId, CancellationToken cancellationToken = default)
    {
        Task<List<int>> providers = GetUserServicesAsync(userId, cancellationToken);
        Task<List<int>> genres = GetUserGenresAsync(userId, cancellationToken);
        Task<UserSettings> settings = GetUserSettingsAsync(userId, cancellationToken);
        await Task.WhenAll(providers, genres, settings);
        return new UserPreferences(providers.Result, genres.Result, settings.Result.EnglishOnly);
    }

    // user services
    public async Task<List<int>> GetUserServicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId AND IsActive = TRUE",
            new { UserId = userId }, cancellationToken: cancellationToken);
        IEnumerable<int> ids = await db.QueryAsync<int>(cmd);
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
    public async Task<List<int>> GetUserGenresAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT GenreId FROM UserGenres WHERE UserId = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        IEnumerable<int> ids = await db.QueryAsync<int>(cmd);
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
    public async Task<ShowSentiments> GetShowSentimentsAsync(int tmdbId, string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition seasonsCmd = new(
            "SELECT TmdbId, SeasonNumber, Sentiment FROM SeasonSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId",
            new { UserId = userId, TmdbId = tmdbId }, cancellationToken: cancellationToken);
        CommandDefinition episodesCmd = new(
            "SELECT TmdbId, SeasonNumber, EpisodeNumber, Sentiment FROM EpisodeSentiments WHERE UserId = @UserId AND TmdbId = @TmdbId",
            new { UserId = userId, TmdbId = tmdbId }, cancellationToken: cancellationToken);
        IEnumerable<SeasonSentiment> seasons = await db.QueryAsync<SeasonSentiment>(seasonsCmd);
        IEnumerable<EpisodeSentiment> episodes = await db.QueryAsync<EpisodeSentiment>(episodesCmd);
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

    // ai usage tracking — CT bounds the write so it can't outlive the drain budget
    // when invoked through AIUsageDrainHostedService.Run (which passes an 8s
    // timeout-bounded token). pins follow-up to #123.
    public async Task TrackAIUsageAsync(string userId, int inputTokens, int outputTokens, decimal costUsd, string requestType, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new("""
            INSERT INTO AIUsage (UserId, InputTokens, OutputTokens, EstimatedCostUsd, RequestType, CreatedAt)
            VALUES (@UserId, @InputTokens, @OutputTokens, @CostUsd, @RequestType, @CreatedAt)
            """, new { UserId = userId, InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = costUsd, RequestType = requestType, CreatedAt = DateTime.UtcNow }, cancellationToken: cancellationToken);
        await db.ExecuteAsync(cmd);
    }

    public async Task<decimal> GetMonthlyAICostAsync(CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM AIUsage WHERE EXTRACT(YEAR FROM CreatedAt) = @Year AND EXTRACT(MONTH FROM CreatedAt) = @Month",
            new { DateTime.UtcNow.Year, DateTime.UtcNow.Month }, cancellationToken: cancellationToken);
        return await db.QuerySingleOrDefaultAsync<decimal>(cmd);
    }

    public async Task<decimal> GetUserAICostAsync(string userId)
    {
        using NpgsqlConnection db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<decimal>(
            "SELECT COALESCE(SUM(EstimatedCostUsd), 0) FROM AIUsage WHERE UserId = @UserId",
            new { UserId = userId });
    }

    // curation cache
    public async Task<(string? rowsJson, string? watchlistHash, DateTime? generatedAt)?> GetCurationCacheAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT RowsJson, WatchlistHash, GeneratedAt FROM CurationCache WHERE UserId = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        (string RowsJson, string WatchlistHash, DateTime GeneratedAt)? row =
            await db.QuerySingleOrDefaultAsync<(string RowsJson, string WatchlistHash, DateTime GeneratedAt)?>(cmd);
        return row;
    }

    // hero rotation: last-shown timestamp per featured title, used to enforce the cooldown.
    // keyed by (TmdbId, MediaType) — TMDb ids are namespaced per media type, so a movie and a
    // TV show sharing a numeric id must each track their own cooldown (#146).
    public async Task<Dictionary<(int TmdbId, string MediaType), DateTime>> GetHeroImpressionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT TmdbId, MediaType, ShownAt FROM CurationHeroImpressions WHERE UserId = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        IEnumerable<(int TmdbId, string MediaType, DateTime ShownAt)> rows = await db.QueryAsync<(int, string, DateTime)>(cmd);
        return rows.ToDictionary(r => (r.TmdbId, r.MediaType), r => r.ShownAt);
    }

    public async Task RecordHeroImpressionAsync(string userId, int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new("""
            INSERT INTO CurationHeroImpressions (UserId, TmdbId, MediaType, ShownAt)
            VALUES (@UserId, @TmdbId, @MediaType, NOW())
            ON CONFLICT (UserId, TmdbId, MediaType) DO UPDATE SET ShownAt = NOW()
            """, new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType }, cancellationToken: cancellationToken);
        await db.ExecuteAsync(cmd);
    }

    public async Task SetCurationCacheAsync(string userId, string rowsJson, string watchlistHash, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new("""
            INSERT INTO CurationCache (UserId, RowsJson, WatchlistHash, GeneratedAt)
            VALUES (@UserId, @RowsJson, @Hash, NOW())
            ON CONFLICT (UserId) DO UPDATE SET RowsJson = @RowsJson, WatchlistHash = @Hash, GeneratedAt = NOW()
            """, new { UserId = userId, RowsJson = rowsJson, Hash = watchlistHash }, cancellationToken: cancellationToken);
        await db.ExecuteAsync(cmd);
    }

    // user settings
    public async Task<UserSettings> GetUserSettingsAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT EnglishOnly, ShowWatching, ShowWantToWatch, ShowFinished, WatchlistSort, MediaType, LastSeenChangelogVersion FROM UserSettings WHERE UserId = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        UserSettings? settings = await db.QuerySingleOrDefaultAsync<UserSettings>(cmd);
        return settings ?? new UserSettings();
    }

    public async Task SaveUserSettingsAsync(string userId, UserSettings settings)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO UserSettings (UserId, EnglishOnly, ShowWatching, ShowWantToWatch, ShowFinished, WatchlistSort, MediaType, LastSeenChangelogVersion)
            VALUES (@UserId, @EnglishOnly, @ShowWatching, @ShowWantToWatch, @ShowFinished, @WatchlistSort, @MediaType, @LastSeenChangelogVersion)
            ON CONFLICT (UserId) DO UPDATE SET
                EnglishOnly              = @EnglishOnly,
                ShowWatching             = @ShowWatching,
                ShowWantToWatch          = @ShowWantToWatch,
                ShowFinished             = @ShowFinished,
                WatchlistSort            = @WatchlistSort,
                MediaType                = @MediaType,
                LastSeenChangelogVersion = @LastSeenChangelogVersion
            """, new
        {
            UserId = userId,
            settings.EnglishOnly,
            settings.ShowWatching,
            settings.ShowWantToWatch,
            settings.ShowFinished,
            settings.WatchlistSort,
            settings.MediaType,
            settings.LastSeenChangelogVersion
        });
    }

    // feedback (#19)
    public async Task SubmitFeedbackAsync(string userId, string category, string message, string? contactEmail)
    {
        using NpgsqlConnection db = await OpenAsync();
        await db.ExecuteAsync("""
            INSERT INTO Feedback (UserId, Category, Message, ContactEmail)
            VALUES (@UserId, @Category, @Message, @ContactEmail)
            """, new { UserId = userId, Category = category, Message = message, ContactEmail = contactEmail });
    }

    public async Task<List<FeedbackItem>> GetFeedbackAsync(CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        // LEFT JOIN so feedback still lists if the user row was since removed; Phone helps an admin
        // see who submitted without surfacing it anywhere else.
        CommandDefinition cmd = new("""
            SELECT F.Id, F.UserId, U.PhoneNumber AS Phone, F.Category, F.Message, F.ContactEmail, F.CreatedAt
            FROM Feedback F
            LEFT JOIN Users U ON U.Id = F.UserId
            ORDER BY F.CreatedAt DESC
            """, cancellationToken: cancellationToken);
        IEnumerable<FeedbackItem> rows = await db.QueryAsync<FeedbackItem>(cmd);
        return [.. rows];
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

    public async Task<List<WatchedEpisode>> GetWatchedEpisodesAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT TmdbId, Season, Episode FROM WatchedEpisodes WHERE UserId = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        IEnumerable<(int TmdbId, int Season, int Episode)> rows = await db.QueryAsync<(int, int, int)>(cmd);
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

    // single-transaction hard delete; gated behind the Admin policy + a type-to-confirm
    // dialog. all rows keyed off the user (queue, sentiments, watched eps, AI usage,
    // curation cache, OTPs) go with them so a re-signup with the same phone starts fresh.
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

        // one statement per ExecuteAsync — explicit, doesn't lean on multi-statement
        // command behavior in any provider/version, easier to add per-table metrics later
        object byUser = new { UserId = userId };
        await db.ExecuteAsync("DELETE FROM QueueItems        WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM UserServices      WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM UserGenres        WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM UserSettings      WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM SeasonSentiments  WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM EpisodeSentiments WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM WatchedEpisodes   WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM AIUsage           WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM CurationCache     WHERE UserId = @UserId", byUser, tx);
        await db.ExecuteAsync("DELETE FROM OtpCodes          WHERE PhoneNumber = @Phone", new { Phone = phone }, tx);
        await db.ExecuteAsync("DELETE FROM Users             WHERE Id = @UserId", byUser, tx);

        await tx.CommitAsync();
        return true;
    }

    // user profile
    public async Task<string?> GetUserFirstNameAsync(string userId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenAsync(cancellationToken);
        CommandDefinition cmd = new(
            "SELECT FirstName FROM Users WHERE Id = @UserId",
            new { UserId = userId }, cancellationToken: cancellationToken);
        return await db.QuerySingleOrDefaultAsync<string?>(cmd);
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

        (decimal aiCost, int aiRequestCount) = await db.QuerySingleAsync<(decimal Cost, int RequestCount)>(
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
            aiCost,
            aiRequestCount);
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

    // accepts ct so cancellation aborts connection acquisition (TCP handshake + auth
    // round-trip) for callers that already abandoned their request. Without this,
    // OpenAsync runs to completion before the subsequent Dapper command throws OCE,
    // wasting a pooled connection on a request the caller has given up on. pins #127.
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        => await _dataSource.OpenConnectionAsync(cancellationToken);

    // single-row lookup by the natural key (UserId, TmdbId, MediaType). Shared by the add
    // and upsert paths and their ON CONFLICT re-reads so the query lives in one place.
    private static Task<QueueItem?> SelectQueueItemAsync(NpgsqlConnection db, string userId, int tmdbId, string mediaType, NpgsqlTransaction? tx = null) =>
        db.QuerySingleOrDefaultAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, TmdbId = tmdbId, MediaType = mediaType }, tx);
}
