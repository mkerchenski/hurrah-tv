using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Changelog;

// parses a Keep-a-Changelog CHANGELOG.md into typed entries (#19). No markdown library — the
// structure is regular. Only `## [version]` headers are releases; a plain `## Heading` (e.g.
// "Keeping this up to date") closes the current release so its bullets aren't captured as content.
public static class ChangelogParser
{
    public static IReadOnlyList<ChangelogEntry> Parse(string? markdown)
    {
        List<ChangelogEntry> entries = [];
        if (string.IsNullOrWhiteSpace(markdown))
            return entries;

        string version = "";
        bool inEntry = false;
        List<ChangelogSection> sections = [];
        string category = "";
        List<string> items = [];

        void FlushSection()
        {
            if (category.Length > 0 && items.Count > 0)
                sections.Add(new ChangelogSection(category, items));
            category = "";
            items = [];
        }

        void FlushEntry()
        {
            FlushSection();
            if (inEntry && sections.Count > 0)
                entries.Add(new ChangelogEntry(version, sections));
            sections = [];
            inEntry = false;
            version = "";
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("## "))
            {
                FlushEntry();
                string head = line[3..].Trim();
                if (head.StartsWith('[') && head.EndsWith(']'))
                {
                    version = head[1..^1].Trim();
                    inEntry = true;
                }
                // a non-bracketed `## Heading` leaves inEntry false, so its content is skipped
            }
            else if (inEntry && line.StartsWith("### "))
            {
                FlushSection();
                category = line[4..].Trim();
            }
            else if (inEntry && category.Length > 0 && line.StartsWith("- "))
            {
                items.Add(line[2..].Trim());
            }
        }

        FlushEntry();
        return entries;
    }

    // the newest *shipped* version — skips [Unreleased] (not yet released), so it drives the
    // new-feature alert banner (#19). Entries are document order (Keep a Changelog lists newest
    // first), so the first dated entry is the latest. Null when nothing has shipped yet.
    public static string? LatestShippedVersion(IReadOnlyList<ChangelogEntry> entries)
    {
        foreach (ChangelogEntry e in entries)
        {
            if (!e.Version.Equals("Unreleased", StringComparison.OrdinalIgnoreCase))
                return e.Version;
        }
        return null;
    }
}
