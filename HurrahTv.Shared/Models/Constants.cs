namespace HurrahTv.Shared.Models;

public static class MediaTypes
{
    public const string Movie = "movie";
    public const string Tv = "tv";
    public const string All = "all";

    public static bool IsValid(string? value) => value is Movie or Tv;
}

public static class ProviderType
{
    public const string Flatrate = "flatrate";
    public const string Ads = "ads";
    public const string Free = "free";
    public const string Buy = "buy";
    public const string Rent = "rent";

    public static readonly string[] All = [Flatrate, Ads, Free, Buy, Rent];
}
