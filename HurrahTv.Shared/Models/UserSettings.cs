namespace HurrahTv.Shared.Models;

public class UserSettings
{
    public bool EnglishOnly { get; set; }

    // home page watchlist row filters — which statuses to show (all default on)
    public bool ShowWatching { get; set; } = true;
    public bool ShowWantToWatch { get; set; } = true;
    public bool ShowFinished { get; set; } = true;
}
