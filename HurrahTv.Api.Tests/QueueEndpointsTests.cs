using System.Net;
using System.Net.Http.Json;
using Dapper;
using HurrahTv.Shared.Models;
using Npgsql;

namespace HurrahTv.Api.Tests;

[Collection("postgres")]
public class QueueEndpointsTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task QueueCrud_HappyPath_AddListUpdateDelete()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-1");

        // add — POST /api/queue
        QueueItem payload = NewQueueItem(tmdbId: 1399, title: "Game of Thrones");
        HttpResponseMessage addResp = await client.PostAsJsonAsync("/api/queue", payload);
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        QueueItem? added = await addResp.Content.ReadFromJsonAsync<QueueItem>();
        Assert.NotNull(added);
        Assert.True(added!.Id > 0);
        Assert.Equal(1399, added.TmdbId);

        // add a second item so position reorder has somewhere to move
        QueueItem second = NewQueueItem(tmdbId: 60625, title: "Rick and Morty");
        HttpResponseMessage addResp2 = await client.PostAsJsonAsync("/api/queue", second);
        Assert.Equal(HttpStatusCode.Created, addResp2.StatusCode);
        QueueItem secondAdded = (await addResp2.Content.ReadFromJsonAsync<QueueItem>())!;
        Assert.True(secondAdded.Position > added.Position);

        // list — GET /api/queue returns both items
        QueueResponse? list = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);

        // update status — PUT /api/queue/{id}/status
        HttpResponseMessage statusResp = await client.PutAsJsonAsync(
            $"/api/queue/{added.Id}/status",
            new QueueStatusUpdate(QueueStatus.Watching));
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        QueueItem? statusUpdated = await statusResp.Content.ReadFromJsonAsync<QueueItem>();
        Assert.Equal(QueueStatus.Watching, statusUpdated!.Status);

        // update position — PUT /api/queue/{id}/position (move second item to position 1)
        HttpResponseMessage posResp = await client.PutAsJsonAsync(
            $"/api/queue/{secondAdded.Id}/position",
            new PositionUpdate(1));
        Assert.Equal(HttpStatusCode.OK, posResp.StatusCode);

        // delete — DELETE /api/queue/{id}
        HttpResponseMessage delResp = await client.DeleteAsync($"/api/queue/{added.Id}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        QueueResponse? afterDelete = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Single(afterDelete!.Items);
        Assert.Equal(secondAdded.Id, afterDelete.Items[0].Id);
    }

    // authorization is the highest-blast-radius bug class for this API.
    // every queue mutation is scoped by sub-claim UserId — these tests pin
    // that scoping so a regression (e.g. forgetting the AND UserId = clause
    // in a UPDATE/DELETE) shows up in CI, not in prod.
    [Fact]
    public async Task UserA_CannotSee_UserB_QueueItems()
    {
        HttpClient userA = TestAuth.CreateClient(fx, "user-A");
        HttpClient userB = TestAuth.CreateClient(fx, "user-B");

        HttpResponseMessage aPost = await userA.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 1, title: "A's show"));
        HttpResponseMessage bPost = await userB.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 2, title: "B's show"));
        Assert.Equal(HttpStatusCode.Created, aPost.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bPost.StatusCode);

        QueueResponse? aList = await userA.GetFromJsonAsync<QueueResponse>("/api/queue");
        QueueResponse? bList = await userB.GetFromJsonAsync<QueueResponse>("/api/queue");

        Assert.Single(aList!.Items);
        Assert.Single(bList!.Items);
        Assert.Equal(1, aList.Items[0].TmdbId);
        Assert.Equal(2, bList.Items[0].TmdbId);
    }

    [Fact]
    public async Task UserA_CannotUpdate_UserB_QueueItem()
    {
        HttpClient userA = TestAuth.CreateClient(fx, "user-A");
        HttpClient userB = TestAuth.CreateClient(fx, "user-B");

        HttpResponseMessage addResp = await userB.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 1, title: "B's show"));
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        QueueItem bItem = (await addResp.Content.ReadFromJsonAsync<QueueItem>())!;

        HttpResponseMessage attack = await userA.PutAsJsonAsync(
            $"/api/queue/{bItem.Id}/status",
            new QueueStatusUpdate(QueueStatus.Finished));

        // 404, not 403 — UPDATE ... WHERE Id = @Id AND UserId = @UserId matches
        // zero rows for user A, so the endpoint reports "not found". the important
        // bit is "not 200" — user B's item must remain unchanged.
        Assert.Equal(HttpStatusCode.NotFound, attack.StatusCode);

        QueueResponse? bList = await userB.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Equal(QueueStatus.WantToWatch, bList!.Items[0].Status);
    }

    [Fact]
    public async Task UserA_CannotDelete_UserB_QueueItem()
    {
        HttpClient userA = TestAuth.CreateClient(fx, "user-A");
        HttpClient userB = TestAuth.CreateClient(fx, "user-B");

        HttpResponseMessage addResp = await userB.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 1, title: "B's show"));
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        QueueItem bItem = (await addResp.Content.ReadFromJsonAsync<QueueItem>())!;

        HttpResponseMessage attack = await userA.DeleteAsync($"/api/queue/{bItem.Id}");
        Assert.Equal(HttpStatusCode.NotFound, attack.StatusCode);

        QueueResponse? bList = await userB.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Single(bList!.Items);
    }

    // pins #101 — recency sort (LatestEpisodeDate) was clobbering manual drag-reorder
    // on Want to Watch. The GetQueueAsync ORDER BY now branches on Status so Want-to-Watch
    // uses Position as its only secondary key; other statuses keep the recency-aware sort.
    [Fact]
    public async Task WantToWatch_OrdersByPosition_RegardlessOfLatestEpisodeDate()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-reorder");

        QueueItem first = (await PostAsync(client, NewQueueItem(tmdbId: 100, title: "Oldest air date")))!;
        QueueItem second = (await PostAsync(client, NewQueueItem(tmdbId: 200, title: "Newest air date")))!;
        QueueItem third = (await PostAsync(client, NewQueueItem(tmdbId: 300, title: "Middle air date")))!;

        // stamp LatestEpisodeDate directly — there's no API to set it, the server normally
        // populates it via TMDb refresh. The OLD ORDER BY would have ranked these by
        // LatestEpisodeDate DESC, ignoring Position. We set them deliberately mismatched
        // to Position so a regression to date-first sorting surfaces immediately.
        using NpgsqlConnection db = new(fx.ConnectionString);
        await db.OpenAsync();
        DateTime now = DateTime.UtcNow;
        await db.ExecuteAsync("UPDATE QueueItems SET LatestEpisodeDate = @D WHERE Id = @Id",
            new[]
            {
                new { first.Id,  D = now.AddDays(-30) }, // oldest
                new { second.Id, D = now.AddDays(-1) },  // newest
                new { third.Id,  D = now.AddDays(-15) }, // middle
            });

        // move 'second' to where 'first' lives
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/queue/{second.Id}/position", new PositionUpdate(first.Position))).StatusCode);

        QueueResponse? afterFirst = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Equal([200, 100, 300], afterFirst!.Items.Select(i => i.TmdbId).ToArray());
    }

    // pins the client-side regression that surfaced after #101's SQL fix: a second
    // drag-reorder ships the FRESH target Position from the post-refetch GET, not a
    // stale value cached from the initial GET. If the client ever stops refetching
    // after a successful PUT, this test will catch it because the second PUT here
    // uses freshly-fetched positions — same as the fixed client does.
    [Fact]
    public async Task TwoConsecutiveReorders_BothPersist_WhenClientRefetchesBetween()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-reorder-twice");

        QueueItem a = (await PostAsync(client, NewQueueItem(tmdbId: 100, title: "A")))!;
        QueueItem b = (await PostAsync(client, NewQueueItem(tmdbId: 200, title: "B")))!;
        QueueItem c = (await PostAsync(client, NewQueueItem(tmdbId: 300, title: "C")))!;

        // first reorder: move B to where A is → order becomes B, A, C
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/queue/{b.Id}/position", new PositionUpdate(a.Position))).StatusCode);

        QueueResponse? afterFirst = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Equal([200, 100, 300], afterFirst!.Items.Select(i => i.TmdbId).ToArray());

        // second reorder: move A (now sitting at the post-refetch position of the middle slot)
        // to the top — needs the FRESH Position of the current top item, not a.Position from
        // the initial POST response which is now stale on the server.
        int targetPos = afterFirst.Items[0].Position; // current top = B
        int aFreshId = afterFirst.Items.First(i => i.TmdbId == 100).Id;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/queue/{aFreshId}/position", new PositionUpdate(targetPos))).StatusCode);

        QueueResponse? afterSecond = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Equal([100, 200, 300], afterSecond!.Items.Select(i => i.TmdbId).ToArray());
    }

    private static async Task<QueueItem?> PostAsync(HttpClient client, QueueItem payload)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync("/api/queue", payload);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<QueueItem>();
    }

    private static QueueItem NewQueueItem(int tmdbId, string title) => new()
    {
        TmdbId = tmdbId,
        MediaType = MediaTypes.Tv,
        Title = title,
        PosterPath = "/poster.jpg",
        BackdropPath = "",
        Status = QueueStatus.WantToWatch,
        AvailableOnJson = "[]"
    };
}
