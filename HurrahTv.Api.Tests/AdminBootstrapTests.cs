using Dapper;
using Npgsql;

namespace HurrahTv.Api.Tests;

// pins #92 — the admin bootstrap that DbService.InitializeAsync runs on startup
// must survive ResetAsync. prod's UPDATE-only bootstrap only flips existing rows,
// so the realistic sequence a test mimics is: insert owner-phone user, then run
// the bootstrap (= what prod does on the next API restart), then assert admin.
// without the fixture surfacing SeedAdminAsync this invariant is unreachable from
// tests and admin-endpoint coverage will produce confusing false negatives.
[Collection("postgres")]
public class AdminBootstrapTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SeedAdmin_FlipsOwnerPhone_AfterResetAndInsert()
    {
        using NpgsqlConnection db = new(fx.ConnectionString);
        await db.OpenAsync();

        // mimic DbService.GetOrCreateUserAsync — creates the row with IsAdmin = FALSE,
        // which is the production state until the next API startup runs the bootstrap.
        await db.ExecuteAsync(
            "INSERT INTO Users (Id, PhoneNumber, CreatedAt) VALUES (@Id, @Phone, NOW())",
            new { Id = "owner-after-reset", Phone = "4406228711" });

        await fx.SeedAdminAsync();

        bool isAdmin = await db.QuerySingleAsync<bool>(
            "SELECT IsAdmin FROM Users WHERE Id = @Id",
            new { Id = "owner-after-reset" });

        Assert.True(isAdmin);
    }
}
