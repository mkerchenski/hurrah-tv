using HurrahTv.Shared.Changelog;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.Caching.Memory;

namespace HurrahTv.Api.Endpoints;

// serves the parsed CHANGELOG.md (#19). Anonymous (no PII) and memory-cached — the content is
// embedded in the assembly, so it only changes on deploy.
public static class ChangelogEndpoints
{
    private const string CacheKey = "changelog:entries";

    public static void MapChangelogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/changelog", (IMemoryCache cache) =>
        {
            IReadOnlyList<ChangelogEntry> entries =
                cache.GetOrCreate(CacheKey, _ => ChangelogParser.Parse(ReadEmbeddedChangelog()))!;
            return Results.Ok(entries);
        }).AllowAnonymous();
    }

    private static string ReadEmbeddedChangelog()
    {
        using Stream? stream = typeof(ChangelogEndpoints).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "";
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
