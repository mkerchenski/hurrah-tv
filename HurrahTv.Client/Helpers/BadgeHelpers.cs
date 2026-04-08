using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Helpers;

public static class BadgeHelpers
{
    public static string StatusBg(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "bg-gray-700/80",
        QueueStatus.Watching    => "bg-green-600/80",
        QueueStatus.Finished    => "bg-blue-600/80",
        QueueStatus.NotForMe    => "bg-gray-600/80",
        _                       => "bg-gray-600/80"
    };

    public static string StatusIcon(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "icon-[heroicons--bookmark-20-solid]",
        QueueStatus.Watching    => "icon-[heroicons--play-20-solid]",
        QueueStatus.Finished    => "icon-[heroicons--check-20-solid]",
        QueueStatus.NotForMe    => "icon-[heroicons--no-symbol-20-solid]",
        _                       => ""
    };

    public static string StatusShortLabel(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "Want",
        QueueStatus.Watching    => "Watching",
        QueueStatus.Finished    => "Watched",
        QueueStatus.NotForMe    => "Nope",
        _                       => ""
    };

    public static string StatusColor(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "text-accent",
        QueueStatus.Watching    => "text-green-400",
        QueueStatus.Finished    => "text-blue-400",
        QueueStatus.NotForMe    => "text-gray-400",
        _                       => "text-gray-400"
    };

    public static string StatusRing(QueueStatus status) => status switch
    {
        QueueStatus.WantToWatch => "ring-accent",
        QueueStatus.Watching    => "ring-green-500",
        QueueStatus.Finished    => "ring-blue-500",
        QueueStatus.NotForMe    => "ring-gray-500",
        _                       => "ring-gray-500"
    };
}
