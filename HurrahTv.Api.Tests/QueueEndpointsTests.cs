using System.Net;
using System.Net.Http.Json;
using HurrahTv.Shared.Models;

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
