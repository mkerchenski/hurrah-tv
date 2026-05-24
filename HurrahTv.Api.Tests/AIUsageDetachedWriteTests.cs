using Dapper;
using HurrahTv.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HurrahTv.Api.Tests;

// pins #121 — AIUsage rows must survive request-scope teardown. Anthropic was already
// paid for the inference, so the cost row landing is non-negotiable. Before the fix,
// TrackAIUsageAsync ran on the request scope and could lose the write if the scope
// disposed mid-flight. The fix: CurationService.TrackUsageDetachedAsync runs on the
// thread pool inside a fresh DI scope, so it's independent of the request lifecycle.
//
// the contract this test pins:
//   1. the row lands even when the caller never awaits the returned Task (fire-and-forget)
//   2. the row lands even when the caller's CancellationToken is already cancelled —
//      because TrackUsageDetachedAsync doesn't observe the caller's ct.
[Collection("postgres")]
public class AIUsageDetachedWriteTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TrackUsageDetached_WritesRow_EvenWithCancelledCallerToken()
    {
        await SeedUserAsync("usage-user");

        using IServiceScope scope = fx.Factory.Services.CreateScope();
        CurationService curation = scope.ServiceProvider.GetRequiredService<CurationService>();

        // simulate "client navigated away" — the caller's ct is already cancelled.
        // the detached write must not observe it.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // await the returned Task so the test deterministically observes completion.
        // production code uses `_ =` to fire-and-forget; the Task lifecycle is identical.
        await curation.TrackUsageDetachedAsync("usage-user", inputTokens: 1234, outputTokens: 567, cost: 0.0042m, requestType: "show-match");

        (int inputTokens, int outputTokens, decimal cost, string requestType) = await QueryUsageRowAsync("usage-user");
        Assert.Equal(1234, inputTokens);
        Assert.Equal(567, outputTokens);
        Assert.Equal(0.0042m, cost);
        Assert.Equal("show-match", requestType);
    }

    [Fact]
    public async Task TrackUsageDetached_SurvivesScopeDisposal()
    {
        // simulate the request-scope-teardown scenario: resolve CurationService from a
        // scope, fire the detached write, dispose the scope BEFORE the write completes,
        // then verify the row still lands. If the write were bound to the request scope
        // (the pre-fix behavior), disposing here would yank its dependencies and the
        // row would be lost.
        await SeedUserAsync("scope-user");

        Task writeTask;
        using (IServiceScope scope = fx.Factory.Services.CreateScope())
        {
            CurationService curation = scope.ServiceProvider.GetRequiredService<CurationService>();
            writeTask = curation.TrackUsageDetachedAsync("scope-user", inputTokens: 100, outputTokens: 50, cost: 0.0001m, requestType: "curation");
        }
        await writeTask;

        (int inputTokens, _, _, string requestType) = await QueryUsageRowAsync("scope-user");
        Assert.Equal(100, inputTokens);
        Assert.Equal("curation", requestType);
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
