namespace HurrahTv.Shared.Models;

// the allowed feedback categories (#19), shared so the server's validation and the client's
// <select> can't drift. These are the wire/storage values (lowercase); user-facing display labels
// stay client-side (see Feedback.razor) per the shared-promotion-semantic-audit learning.
public static class FeedbackCategories
{
    public const string General = "general";
    public const string Bug = "bug";
    public const string Feature = "feature";

    // order drives the client dropdown; General first — it's also the server's safe fallback.
    public static readonly IReadOnlyList<string> All = [General, Bug, Feature];
}
