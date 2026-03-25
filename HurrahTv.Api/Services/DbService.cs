using Dapper;
using Microsoft.Data.Sqlite;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class DbService
{
    private readonly string _connectionString;

    public DbService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default") ?? "Data Source=hurrahtv.db";
    }

    public async Task InitializeAsync()
    {
        using SqliteConnection db = Open();

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS QueueItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL DEFAULT 'default',
                TmdbId INTEGER NOT NULL,
                MediaType TEXT NOT NULL,
                Title TEXT NOT NULL,
                PosterPath TEXT NOT NULL DEFAULT '',
                Position INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                AvailableOnJson TEXT NOT NULL DEFAULT '[]',
                AddedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS UserServices (
                UserId TEXT NOT NULL DEFAULT 'default',
                ProviderId INTEGER NOT NULL,
                PRIMARY KEY (UserId, ProviderId)
            );

            CREATE INDEX IF NOT EXISTS IX_QueueItems_UserId ON QueueItems(UserId);
            """);
    }

    // queue operations
    public async Task<List<QueueItem>> GetQueueAsync(string userId = "default")
    {
        using SqliteConnection db = Open();
        IEnumerable<QueueItem> items = await db.QueryAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE UserId = @UserId ORDER BY Status, Position",
            new { UserId = userId });
        return items.ToList();
    }

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item, string userId = "default")
    {
        using SqliteConnection db = Open();

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
            AddedAt = DateTime.UtcNow.ToString("o")
        });

        item.Id = id;
        return item;
    }

    public async Task<bool> RemoveFromQueueAsync(int id, string userId = "default")
    {
        using SqliteConnection db = Open();
        int affected = await db.ExecuteAsync(
            "DELETE FROM QueueItems WHERE Id = @Id AND UserId = @UserId",
            new { Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> UpdateStatusAsync(int id, QueueStatus status, string userId = "default")
    {
        using SqliteConnection db = Open();
        int affected = await db.ExecuteAsync(
            "UPDATE QueueItems SET Status = @Status WHERE Id = @Id AND UserId = @UserId",
            new { Status = (int)status, Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> ReorderAsync(int id, int newPosition, string userId = "default")
    {
        using SqliteConnection db = Open();
        QueueItem? item = await db.QuerySingleOrDefaultAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE Id = @Id AND UserId = @UserId",
            new { Id = id, UserId = userId });

        if (item == null) return false;

        int oldPosition = item.Position;
        if (newPosition == oldPosition) return true;

        if (newPosition < oldPosition)
        {
            await db.ExecuteAsync(
                "UPDATE QueueItems SET Position = Position + 1 WHERE UserId = @UserId AND Position >= @New AND Position < @Old",
                new { UserId = userId, New = newPosition, Old = oldPosition });
        }
        else
        {
            await db.ExecuteAsync(
                "UPDATE QueueItems SET Position = Position - 1 WHERE UserId = @UserId AND Position > @Old AND Position <= @New",
                new { UserId = userId, Old = oldPosition, New = newPosition });
        }

        await db.ExecuteAsync(
            "UPDATE QueueItems SET Position = @Position WHERE Id = @Id",
            new { Position = newPosition, Id = id });

        return true;
    }

    // user services
    public async Task<List<int>> GetUserServicesAsync(string userId = "default")
    {
        using SqliteConnection db = Open();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId",
            new { UserId = userId });
        return ids.ToList();
    }

    public async Task SetUserServicesAsync(List<int> providerIds, string userId = "default")
    {
        using SqliteConnection db = Open();
        await db.ExecuteAsync("DELETE FROM UserServices WHERE UserId = @UserId", new { UserId = userId });

        foreach (int pid in providerIds)
        {
            await db.ExecuteAsync(
                "INSERT INTO UserServices (UserId, ProviderId) VALUES (@UserId, @ProviderId)",
                new { UserId = userId, ProviderId = pid });
        }
    }

    private SqliteConnection Open()
    {
        SqliteConnection conn = new(_connectionString);
        conn.Open();
        return conn;
    }
}
