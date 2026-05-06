namespace HurrahTv.Shared.Models;

public record AdminUsersResponse(int TotalUsers, List<AdminUserSummary> Users);

public record AdminUserSummary(
    string Id,
    string PhoneNumber,
    string? FirstName,
    DateTime CreatedAt,
    bool IsAdmin,
    int QueueCount,
    int ActiveServiceCount,
    decimal TotalAiCostUsd,
    DateTime? LastQueueAddAt
);

public record AdminSetAdminRequest(bool IsAdmin);

public record AdminSetFirstNameRequest(string? FirstName);

public record AdminUserDetail(
    string Id,
    string PhoneNumber,
    string? FirstName,
    DateTime CreatedAt,
    bool IsAdmin,
    List<AdminQueueRow> Queue,
    List<int> Services,
    List<int> Genres,
    int SeasonSentimentCount,
    int EpisodeSentimentCount,
    int WatchedEpisodeCount,
    decimal TotalAiCostUsd,
    int AiRequestCount
);

public record AdminQueueRow(
    int Id,
    int TmdbId,
    string MediaType,
    string Title,
    int Status,
    int? Sentiment,
    DateTime AddedAt
);

public record AdminAiUsageBucket(
    DateTime BucketStart,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    int RequestCount,
    int DistinctUsers
);

public record AdminAiUsageResponse(
    decimal MonthToDateCostUsd,
    decimal MonthlyBudgetUsd,
    List<AdminAiUsageBucket> Daily,
    List<AdminAiUsageBucket> Weekly,
    List<AdminAiUsageBucket> Monthly,
    List<AdminAiUsageByType> ByType
);

public record AdminAiUsageByType(string RequestType, int RequestCount, decimal CostUsd);

public record AdminOnboardingFunnel(
    int TotalSignups,
    int WithServices,
    int WithGenres,
    int WithFirstQueueAdd,
    List<AdminSignupBucket> SignupsByDay
);

public record AdminSignupBucket(DateTime Day, int Count);
