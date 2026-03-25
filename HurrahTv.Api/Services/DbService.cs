using Dapper;
using Microsoft.Data.SqlClient;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class DbService
{
    private readonly string _connectionString;

    public DbService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required");
    }

    public async Task InitializeAsync()
    {
        using SqlConnection db = Open();

        await db.ExecuteAsync("""
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
            CREATE TABLE Users (
                Id NVARCHAR(50) PRIMARY KEY,
                PhoneNumber NVARCHAR(20) NOT NULL UNIQUE,
                CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
            );

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OtpCodes')
            CREATE TABLE OtpCodes (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                PhoneNumber NVARCHAR(20) NOT NULL,
                Code NVARCHAR(10) NOT NULL,
                ExpiresAt DATETIME2 NOT NULL,
                Used BIT NOT NULL DEFAULT 0
            );

            IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_OtpCodes_Phone')
            CREATE INDEX IX_OtpCodes_Phone ON OtpCodes(PhoneNumber, Used);

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'QueueItems')
            CREATE TABLE QueueItems (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UserId NVARCHAR(50) NOT NULL,
                TmdbId INT NOT NULL,
                MediaType NVARCHAR(10) NOT NULL,
                Title NVARCHAR(500) NOT NULL,
                PosterPath NVARCHAR(500) NOT NULL DEFAULT '',
                Position INT NOT NULL DEFAULT 0,
                Status INT NOT NULL DEFAULT 0,
                AvailableOnJson NVARCHAR(MAX) NOT NULL DEFAULT '[]',
                AddedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
            );

            IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_QueueItems_UserId')
            CREATE INDEX IX_QueueItems_UserId ON QueueItems(UserId);

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'UserServices')
            CREATE TABLE UserServices (
                UserId NVARCHAR(50) NOT NULL,
                ProviderId INT NOT NULL,
                PRIMARY KEY (UserId, ProviderId)
            );
            """);
    }

    // queue operations
    public async Task<List<QueueItem>> GetQueueAsync(string userId)
    {
        using SqlConnection db = Open();
        IEnumerable<QueueItem> items = await db.QueryAsync<QueueItem>(
            "SELECT * FROM QueueItems WHERE UserId = @UserId ORDER BY Status, Position",
            new { UserId = userId });
        return items.ToList();
    }

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item, string userId)
    {
        using SqlConnection db = Open();

        // check for duplicate
        int? existing = await db.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM QueueItems WHERE UserId = @UserId AND TmdbId = @TmdbId AND MediaType = @MediaType",
            new { UserId = userId, item.TmdbId, item.MediaType });

        if (existing != null) return null;

        // get next position
        int maxPos = await db.QuerySingleOrDefaultAsync<int>(
            "SELECT ISNULL(MAX(Position), 0) FROM QueueItems WHERE UserId = @UserId",
            new { UserId = userId });

        item.Position = maxPos + 1;

        int id = await db.QuerySingleAsync<int>("""
            INSERT INTO QueueItems (UserId, TmdbId, MediaType, Title, PosterPath, Position, Status, AvailableOnJson, AddedAt)
            OUTPUT INSERTED.Id
            VALUES (@UserId, @TmdbId, @MediaType, @Title, @PosterPath, @Position, @Status, @AvailableOnJson, @AddedAt)
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
        using SqlConnection db = Open();
        int affected = await db.ExecuteAsync(
            "DELETE FROM QueueItems WHERE Id = @Id AND UserId = @UserId",
            new { Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> UpdateStatusAsync(int id, QueueStatus status, string userId)
    {
        using SqlConnection db = Open();
        int affected = await db.ExecuteAsync(
            "UPDATE QueueItems SET Status = @Status WHERE Id = @Id AND UserId = @UserId",
            new { Status = (int)status, Id = id, UserId = userId });
        return affected > 0;
    }

    public async Task<bool> ReorderAsync(int id, int newPosition, string userId)
    {
        using SqlConnection db = Open();
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
    public async Task<List<int>> GetUserServicesAsync(string userId)
    {
        using SqlConnection db = Open();
        IEnumerable<int> ids = await db.QueryAsync<int>(
            "SELECT ProviderId FROM UserServices WHERE UserId = @UserId",
            new { UserId = userId });
        return ids.ToList();
    }

    public async Task SetUserServicesAsync(List<int> providerIds, string userId)
    {
        using SqlConnection db = Open();
        await db.ExecuteAsync("DELETE FROM UserServices WHERE UserId = @UserId", new { UserId = userId });

        foreach (int pid in providerIds)
        {
            await db.ExecuteAsync(
                "INSERT INTO UserServices (UserId, ProviderId) VALUES (@UserId, @ProviderId)",
                new { UserId = userId, ProviderId = pid });
        }
    }

    // auth operations
    public async Task SaveOtpCodeAsync(string phoneNumber, string code)
    {
        using SqlConnection db = Open();

        // invalidate previous unused codes for this phone
        await db.ExecuteAsync(
            "UPDATE OtpCodes SET Used = 1 WHERE PhoneNumber = @PhoneNumber AND Used = 0",
            new { PhoneNumber = phoneNumber });

        await db.ExecuteAsync(
            "INSERT INTO OtpCodes (PhoneNumber, Code, ExpiresAt) VALUES (@PhoneNumber, @Code, @ExpiresAt)",
            new { PhoneNumber = phoneNumber, Code = code, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
    }

    public async Task<bool> VerifyOtpCodeAsync(string phoneNumber, string code)
    {
        using SqlConnection db = Open();

        int? id = await db.QuerySingleOrDefaultAsync<int?>(
            "SELECT Id FROM OtpCodes WHERE PhoneNumber = @PhoneNumber AND Code = @Code AND Used = 0 AND ExpiresAt > @Now",
            new { PhoneNumber = phoneNumber, Code = code, Now = DateTime.UtcNow });

        if (id == null) return false;

        await db.ExecuteAsync("UPDATE OtpCodes SET Used = 1 WHERE Id = @Id", new { Id = id });
        return true;
    }

    public async Task<string> GetOrCreateUserAsync(string phoneNumber)
    {
        using SqlConnection db = Open();

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

    private SqlConnection Open()
    {
        SqlConnection conn = new(_connectionString);
        conn.Open();
        return conn;
    }
}
