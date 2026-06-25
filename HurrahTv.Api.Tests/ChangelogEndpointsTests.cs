using System.Net.Http.Json;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Tests;

// #19 Phase 4 — proves the embedded CHANGELOG.md resource resolves (LogicalName "CHANGELOG.md") and
// /api/changelog parses + serves it anonymously end-to-end. A parser unit test alone wouldn't catch
// a wrong embed name (the stream would be null and the endpoint would silently return []).
[Collection("postgres")]
public class ChangelogEndpointsTests(PostgresFixture fx)
{
    [Fact]
    public async Task GetChangelog_Anonymous_ReturnsParsedEntries()
    {
        HttpClient client = fx.Factory.CreateClient(); // no auth header — endpoint is anonymous
        List<ChangelogEntry>? entries = await client.GetFromJsonAsync<List<ChangelogEntry>>("/api/changelog");

        Assert.NotNull(entries);
        Assert.NotEmpty(entries!); // the real CHANGELOG.md has at least an [Unreleased] section
    }
}
