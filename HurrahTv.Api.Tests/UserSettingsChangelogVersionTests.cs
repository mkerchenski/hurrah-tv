using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// #19 Phase 1 — the new LastSeenChangelogVersion column must round-trip through
// Get/SaveUserSettingsAsync (it drives the new-feature alert banner's "have I seen this?" check),
// and widening the SELECT/INSERT must not regress the existing settings columns.
[Collection("postgres")]
public class UserSettingsChangelogVersionTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LastSeenChangelogVersion_RoundTrips()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        UserSettings fresh = await db.GetUserSettingsAsync("changelog-user");
        Assert.Null(fresh.LastSeenChangelogVersion); // never seen → null

        fresh.LastSeenChangelogVersion = "2026-06-25";
        await db.SaveUserSettingsAsync("changelog-user", fresh);

        UserSettings reloaded = await db.GetUserSettingsAsync("changelog-user");
        Assert.Equal("2026-06-25", reloaded.LastSeenChangelogVersion);
    }

    [Fact]
    public async Task Widening_DoesNotRegress_ExistingSettings()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        UserSettings settings = new()
        {
            EnglishOnly = true,
            WatchlistSort = "sentiment",
            LastSeenChangelogVersion = null
        };
        await db.SaveUserSettingsAsync("changelog-user-2", settings);

        UserSettings reloaded = await db.GetUserSettingsAsync("changelog-user-2");
        Assert.Null(reloaded.LastSeenChangelogVersion);
        Assert.True(reloaded.EnglishOnly);
        Assert.Equal("sentiment", reloaded.WatchlistSort);
    }
}
