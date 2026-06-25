using System.Net;
using System.Net.Http.Json;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// #19 Phase 2 — feedback submit (auth + honeypot + validation) and admin read. Each test uses a
// distinct user id, so the per-user rate limit (5/5min) never trips across the suite.
[Collection("postgres")]
public class FeedbackEndpointsTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private DbService Db => fx.Factory.Services.GetRequiredService<DbService>();

    [Fact]
    public async Task DbRoundTrip_Submit_Then_GetFeedback_ReturnsIt()
    {
        await Db.SubmitFeedbackAsync("fb-user", "bug", "it broke", "me@example.com");

        List<FeedbackItem> all = await Db.GetFeedbackAsync();
        FeedbackItem? item = all.FirstOrDefault(f => f.UserId == "fb-user");
        Assert.NotNull(item);
        Assert.Equal("bug", item!.Category);
        Assert.Equal("it broke", item.Message);
        Assert.Equal("me@example.com", item.ContactEmail);
        Assert.Null(item.Phone); // no Users row → LEFT JOIN yields null, query still returns the row
    }

    [Fact]
    public async Task Post_ValidFeedback_StoresIt()
    {
        HttpClient client = TestAuth.CreateClient(fx, "http-fb-user");
        HttpResponseMessage res = await client.PostAsJsonAsync("/api/feedback",
            new FeedbackSubmission { Category = "feature", Message = "add a dark mode toggle" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        List<FeedbackItem> all = await Db.GetFeedbackAsync();
        Assert.Contains(all, f => f.UserId == "http-fb-user" && f.Message == "add a dark mode toggle" && f.Category == "feature");
    }

    [Fact]
    public async Task Post_HoneypotFilled_DroppedSilently()
    {
        HttpClient client = TestAuth.CreateClient(fx, "bot-user");
        HttpResponseMessage res = await client.PostAsJsonAsync("/api/feedback",
            new FeedbackSubmission { Category = "general", Message = "spam", Website = "http://spam.example" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // 200 so the bot can't tell it was filtered

        List<FeedbackItem> all = await Db.GetFeedbackAsync();
        Assert.DoesNotContain(all, f => f.UserId == "bot-user"); // ...but nothing was stored
    }

    [Fact]
    public async Task Post_EmptyMessage_BadRequest()
    {
        HttpClient client = TestAuth.CreateClient(fx, "empty-user");
        HttpResponseMessage res = await client.PostAsJsonAsync("/api/feedback",
            new FeedbackSubmission { Category = "bug", Message = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_UnknownCategory_FallsBackToGeneral()
    {
        HttpClient client = TestAuth.CreateClient(fx, "cat-user");
        await client.PostAsJsonAsync("/api/feedback",
            new FeedbackSubmission { Category = "totally-made-up", Message = "hello" });

        List<FeedbackItem> all = await Db.GetFeedbackAsync();
        FeedbackItem? item = all.FirstOrDefault(f => f.UserId == "cat-user");
        Assert.NotNull(item);
        Assert.Equal("general", item!.Category); // never reject real feedback over a bad label
    }

    [Fact]
    public async Task AdminFeedback_RejectsNonAdmin()
    {
        HttpClient client = TestAuth.CreateClient(fx, "non-admin-user");
        HttpResponseMessage res = await client.GetAsync("/api/admin/feedback");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // #225 (xreview): deleting a user must also remove their feedback — the Feedback table was
    // added in #19 but the DeleteUserAsync cascade didn't include it, orphaning rows in the admin view.
    [Fact]
    public async Task DeleteUser_AlsoRemovesTheirFeedback_PinsIssue225()
    {
        string userId = await Db.GetOrCreateUserAsync("+15555550199");
        await Db.SubmitFeedbackAsync(userId, "bug", "should be deleted with the user", null);
        Assert.Contains(await Db.GetFeedbackAsync(), f => f.UserId == userId);

        Assert.True(await Db.DeleteUserAsync(userId));

        Assert.DoesNotContain(await Db.GetFeedbackAsync(), f => f.UserId == userId);
    }
}
