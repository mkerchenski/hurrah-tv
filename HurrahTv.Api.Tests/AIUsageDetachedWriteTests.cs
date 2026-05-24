using Dapper;
using HurrahTv.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HurrahTv.Api.Tests;

// pins #121 — AIUsage rows must survive request-scope teardown. Anthropic was already
// paid for the inference, so the cost row landing is non-negotiable. CurationService
// .TrackUsageDetachedAsync runs the write on the thread pool inside a fresh DI scope
// (a no-op today because DbService is singleton, but future-proof if it goes scoped).
//
// the contract this test pins: the row lands. The method has no CancellationToken
// parameter — callers physically can't propagate their cancellation into it — so
// we don't test "ignores caller ct" here. That property is enforced by the signature.
[Collection("postgres")]
public class AIUsageDetachedWriteTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TrackUsageDetached_WritesRow()
    {
        await SeedUserAsync("usage-user");

        using IServiceScope scope = fx.Factory.Services.CreateScope();
        CurationService curation = scope.ServiceProvider.GetRequiredService<CurationService>();

        // await deterministically so the row is durable before assertion;
        // production code uses `_ =` to fire-and-forget.
        await curation.TrackUsageDetachedAsync("usage-user", inputTokens: 1234, outputTokens: 567, cost: 0.0042m, requestType: "show-match");

        (int inputTokens, int outputTokens, decimal cost, string requestType) = await QueryUsageRowAsync("usage-user");
        Assert.Equal(1234, inputTokens);
        Assert.Equal(567, outputTokens);
        Assert.Equal(0.0042m, cost);
        Assert.Equal("show-match", requestType);
    }

    private async Task SeedUserAsync(string userId)
    {
        using NpgsqlConnection db = new(fx.ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "INSERT INTO Users (Id, PhoneNumber, CreatedAt) VALUES (@Id, @Phone, @Now)",
            new { Id = userId, Phone = userId, Now = DateTime.UtcNow });
    }

    private async Task<(int inputTokens, int outputTokens, decimal cost, string requestType)> QueryUsageRowAsync(string userId)
    {
        using NpgsqlConnection db = new(fx.ConnectionString);
        await db.OpenAsync();
        return await db.QuerySingleAsync<(int, int, decimal, string)>(
            "SELECT InputTokens, OutputTokens, EstimatedCostUsd, RequestType FROM AIUsage WHERE UserId = @UserId",
            new { UserId = userId });
    }
}
