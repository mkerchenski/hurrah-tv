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
        object[] updates =
        [
            new { first.Id,  D = now.AddDays(-30) }, // oldest
            new { second.Id, D = now.AddDays(-1) },  // newest
            new { third.Id,  D = now.AddDays(-15) }, // middle
        ];
        await db.ExecuteAsync("UPDATE QueueItems SET LatestEpisodeDate = @D WHERE Id = @Id", updates);

        // move 'second' to where 'first' lives
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/queue/{second.Id}/position", new PositionUpdate(first.Position))).StatusCode);

        QueueResponse? afterFirst = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        int[] firstOrder = [.. afterFirst!.Items.Select(i => i.TmdbId)];
        Assert.Equal([200, 100, 300], firstOrder);
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
        int[] firstOrder = [.. afterFirst!.Items.Select(i => i.TmdbId)];
        Assert.Equal([200, 100, 300], firstOrder);

        // second reorder: move A (now sitting at the post-refetch position of the middle slot)
        // to the top — needs the FRESH Position of the current top item, not a.Position from
        // the initial POST response which is now stale on the server.
        int targetPos = afterFirst.Items[0].Position; // current top = B
        int aFreshId = afterFirst.Items.First(i => i.TmdbId == 100).Id;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/queue/{aFreshId}/position", new PositionUpdate(targetPos))).StatusCode);

        QueueResponse? afterSecond = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        int[] secondOrder = [.. afterSecond!.Items.Select(i => i.TmdbId)];
        Assert.Equal([100, 200, 300], secondOrder);
    }

    // pins #155 — duplicate adds are idempotent at the DB level. A double-tap, two tabs, or a
    // re-add from another surface must collapse to ONE (UserId, TmdbId, MediaType) row, and the
    // second add returns the existing item as success (not a 409).
    [Fact]
    public async Task DuplicateAdd_IsIdempotent_OneRow_SecondReturnsExisting()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-dedup");

        QueueItem payload = NewQueueItem(tmdbId: 1399, title: "Game of Thrones");
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/queue", payload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        QueueItem firstItem = (await first.Content.ReadFromJsonAsync<QueueItem>())!;

        // second add of the same (TmdbId, MediaType) — must NOT be a 409, and must return the
        // already-queued row rather than creating a duplicate.
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/queue", payload);
        Assert.NotEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.True(second.IsSuccessStatusCode);
        QueueItem secondItem = (await second.Content.ReadFromJsonAsync<QueueItem>())!;
        Assert.Equal(firstItem.Id, secondItem.Id);

        QueueResponse? list = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Single(list!.Items);
    }

    // pins #155 — the UNIQUE constraint + ON CONFLICT must hold under a race. Fire several
    // concurrent adds of the same title (the double-tap / two-tabs scenario) and assert exactly
    // one row lands. Before the constraint, two SELECT-then-INSERT calls could both pass the
    // application-level dup check and write two rows.
    [Fact]
    public async Task ConcurrentAdds_SameTitle_ResultInExactlyOneRow()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-race");
        QueueItem payload = NewQueueItem(tmdbId: 1399, title: "Game of Thrones");

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/queue", payload)));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"unexpected {(int)r.StatusCode}"));

        QueueResponse? list = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Single(list!.Items);
    }

    // pins #163 — concurrent adds of DISTINCT titles for one user must land on distinct
    // Position values. Before the per-user advisory lock, both calls read the same
    // COALESCE(MAX(Position),0) before either INSERT committed and wrote the same Position,
    // leaving GetQueueAsync's Position-keyed ORDER BY to pick an arbitrary relative order.
    [Fact]
    public async Task ConcurrentAdds_DistinctTitles_GetDistinctPositions()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-pos-race");

        const int n = 8;
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, n).Select(i =>
                client.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 1000 + i, title: $"Show {i}"))));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"unexpected {(int)r.StatusCode}"));

        QueueResponse? list = await client.GetFromJsonAsync<QueueResponse>("/api/queue");
        Assert.Equal(n, list!.Items.Count);

        int[] positions = [.. list.Items.Select(i => i.Position)];
        Assert.Equal(n, positions.Distinct().Count());
    }

    // pins #183 — UpsertWithStatusAsync's ApplyUpdatePolicy was refactored to thread the caller's
    // transaction through (so the race-lost path's UPDATE stays inside the advisory lock). These
    // pin that the policy behavior #155 codified is unchanged on the fast path (existing row): a
    // forgotten tx arg or a broken reorder would surface as a wrong status / missing backfill here.
    [Fact]
    public async Task Seen_OnExistingItem_TransitionsToFinished_AndBackfillsBackdrop()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-seen");

        // add as WantToWatch with no backdrop, then /seen with a backdrop → status flips to
        // Finished AND the empty backdrop is backfilled (the willUpdate && needsBackdropBackfill branch)
        QueueItem added = (await PostAsync(client, NewQueueItem(tmdbId: 1399, title: "Game of Thrones")))!;
        Assert.Equal(QueueStatus.WantToWatch, added.Status);
        Assert.Equal("", added.BackdropPath);

        HttpResponseMessage seenResp = await client.PostAsJsonAsync("/api/queue/seen",
            new SeenRequest(1399, MediaTypes.Tv, "Game of Thrones", "/poster.jpg", "[]", BackdropPath: "/backdrop.jpg"));
        Assert.Equal(HttpStatusCode.OK, seenResp.StatusCode);
        QueueItem seen = (await seenResp.Content.ReadFromJsonAsync<QueueItem>())!;

        Assert.Equal(added.Id, seen.Id); // same row — fast path, no duplicate
        Assert.Equal(QueueStatus.Finished, seen.Status);
        Assert.Equal("/backdrop.jpg", seen.BackdropPath);
    }

    // pins #183 — /ensure never changes status (shouldUpdate => false) but must still backfill a
    // missing backdrop (the needsBackdropBackfill-only branch). Guards the backdrop-only path of
    // ApplyUpdatePolicy under the tx-threading refactor.
    [Fact]
    public async Task Ensure_OnExistingItem_LeavesStatus_ButBackfillsBackdrop()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-ensure");

        QueueItem added = (await PostAsync(client, NewQueueItem(tmdbId: 1399, title: "Game of Thrones")))!;

        // move it to Watching so we can prove /ensure does NOT touch status
        HttpResponseMessage statusResp = await client.PutAsJsonAsync(
            $"/api/queue/{added.Id}/status", new QueueStatusUpdate(QueueStatus.Watching));
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

        HttpResponseMessage ensureResp = await client.PostAsJsonAsync("/api/queue/ensure",
            new SeenRequest(1399, MediaTypes.Tv, "Game of Thrones", "/poster.jpg", "[]", BackdropPath: "/backdrop.jpg"));
        Assert.Equal(HttpStatusCode.OK, ensureResp.StatusCode);
        QueueItem ensured = (await ensureResp.Content.ReadFromJsonAsync<QueueItem>())!;

        Assert.Equal(added.Id, ensured.Id);
        Assert.Equal(QueueStatus.Watching, ensured.Status); // status untouched by /ensure
        Assert.Equal("/backdrop.jpg", ensured.BackdropPath); // but backdrop backfilled
    }

    // pins #8 — the targeted GET /api/queue/{tmdbId}/{mediaType} the Details page uses instead
    // of fetching the whole watchlist. Returns the item when queued, 404 when not, and 400 for an
    // invalid media type.
    [Fact]
    public async Task GetQueueItem_ReturnsItem_WhenQueued_404_WhenNot()
    {
        HttpClient client = TestAuth.CreateClient(fx, "user-getitem");

        QueueItem added = (await PostAsync(client, NewQueueItem(tmdbId: 1399, title: "Game of Thrones")))!;

        // queued → 200 with the row
        HttpResponseMessage hit = await client.GetAsync($"/api/queue/1399/{MediaTypes.Tv}");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        QueueItem? fetched = await hit.Content.ReadFromJsonAsync<QueueItem>();
        Assert.Equal(added.Id, fetched!.Id);
        Assert.Equal(1399, fetched.TmdbId);

        // not queued (wrong id) → 404
        HttpResponseMessage missId = await client.GetAsync($"/api/queue/2/{MediaTypes.Tv}");
        Assert.Equal(HttpStatusCode.NotFound, missId.StatusCode);

        // right id, wrong media type → 404 (the dedup key includes MediaType)
        HttpResponseMessage missType = await client.GetAsync($"/api/queue/1399/{MediaTypes.Movie}");
        Assert.Equal(HttpStatusCode.NotFound, missType.StatusCode);

        // invalid media type → 400
        HttpResponseMessage bad = await client.GetAsync("/api/queue/1399/banana");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // the targeted lookup must be UserId-scoped like every other queue read — user A asking for a
    // tmdbId only user B has queued gets a 404, never B's row.
    [Fact]
    public async Task GetQueueItem_IsUserScoped()
    {
        HttpClient userA = TestAuth.CreateClient(fx, "user-A");
        HttpClient userB = TestAuth.CreateClient(fx, "user-B");

        HttpResponseMessage bPost = await userB.PostAsJsonAsync("/api/queue", NewQueueItem(tmdbId: 1399, title: "B's show"));
        Assert.Equal(HttpStatusCode.Created, bPost.StatusCode);

        HttpResponseMessage aLookup = await userA.GetAsync($"/api/queue/1399/{MediaTypes.Tv}");
        Assert.Equal(HttpStatusCode.NotFound, aLookup.StatusCode);
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
