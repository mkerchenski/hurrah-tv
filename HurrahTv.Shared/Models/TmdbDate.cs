using System.Globalization;

namespace HurrahTv.Shared.Models;

// Canonical parse for TMDb date-only fields (air_date / first_air_date), shared by Api and Client
// so both sides of the wire agree on the convention. These fields carry no hour-of-day; parse with
// AssumeUniversal|AdjustToUniversal so the value is Kind=Utc — an Unspecified-Kind value can drift
// a calendar day against a UTC "today". See Learnings/tmdb-air-date-is-date-only.md.
public static class TmdbDate
{
    public static bool TryParse(string? raw, out DateTime parsed) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
}
