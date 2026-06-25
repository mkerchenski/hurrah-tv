using HurrahTv.Shared.Changelog;
using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// #19 Phase 4 — the CHANGELOG.md parser. Pinning the load-bearing cases: only `## [version]` are
// releases, `[Unreleased]` is excluded from "latest shipped", and a plain `## Heading` (the
// "Keeping this up to date" footer) doesn't leak its bullets into a release.
public class ChangelogParserTests
{
    private const string Sample = """
        # Changelog

        Intro text, ignored.

        ## [Unreleased]

        ### Added
        - Contributor onboarding
        - 24 issues migrated

        ### Changed
        - Line endings normalized

        ---

        ## [2026-05-01]

        ### Fixed
        - Slow Details page

        ## Keeping this up to date

        - This bullet is NOT a release entry
        """;

    [Fact]
    public void Parse_ReadsReleases_Sections_Items()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse(Sample);

        Assert.Equal(2, entries.Count); // Unreleased + dated; the footer heading is not a release
        ChangelogEntry unreleased = entries[0];
        Assert.Equal("Unreleased", unreleased.Version);
        Assert.Equal(2, unreleased.Sections.Count);
        Assert.Equal("Added", unreleased.Sections[0].Category);
        Assert.Equal(2, unreleased.Sections[0].Items.Count);
        Assert.Contains("Contributor onboarding", unreleased.Sections[0].Items);
        Assert.Equal("Changed", unreleased.Sections[1].Category);
    }

    [Fact]
    public void Parse_NonBracketedHeading_DoesNotLeakIntoPriorRelease()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse(Sample);

        Assert.DoesNotContain(entries, e => e.Version.Contains("Keeping"));
        ChangelogEntry dated = entries.Single(e => e.Version == "2026-05-01");
        Assert.Single(dated.Sections); // just "Fixed" — the footer bullet is not attached
        Assert.DoesNotContain(dated.Sections.SelectMany(s => s.Items), i => i.Contains("NOT a release"));
    }

    [Fact]
    public void LatestShippedVersion_SkipsUnreleased()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse(Sample);
        Assert.Equal("2026-05-01", ChangelogParser.LatestShippedVersion(entries));
    }

    [Fact]
    public void LatestShippedVersion_NullWhenOnlyUnreleased()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse("## [Unreleased]\n### Added\n- a thing\n");
        Assert.Equal("Unreleased", Assert.Single(entries).Version);
        Assert.Null(ChangelogParser.LatestShippedVersion(entries));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# Changelog\n\nNo entries yet.")]
    public void Parse_EmptyOrNoEntries_ReturnsEmpty(string markdown) =>
        Assert.Empty(ChangelogParser.Parse(markdown));
}
