using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Helpers;

public static class SentimentHelpers
{
    public static string Icon(int sentiment) => sentiment switch
    {
        SentimentLevel.Down => "icon-[heroicons--hand-thumb-down-20-solid]",
        SentimentLevel.Up => "icon-[heroicons--hand-thumb-up-20-solid]",
        SentimentLevel.Favorite => "icon-[heroicons--heart-20-solid]",
        _ => ""
    };

    public static string Color(int sentiment) => sentiment switch
    {
        SentimentLevel.Down => "text-red-400",
        SentimentLevel.Up => "text-green-400",
        SentimentLevel.Favorite => "text-pink-400",
        _ => ""
    };

    public static string StatusLabel(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "Want to Watch",
        QueueStatus.Watching => "Watching",
        QueueStatus.Finished => "Watched",
        QueueStatus.NotForMe => "Not For Me",
        _ => ""
    };
}
