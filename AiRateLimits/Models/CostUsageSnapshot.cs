namespace AiRateLimits.Models;

/// <summary>
/// Informational monthly spend estimate derived from local logs and public model prices.
/// Must not influence health, warnings, tray color, taskbar overlay, or notifications.
/// </summary>
public sealed record CostUsageSnapshot(
    decimal MonthCostUsd,
    long MonthTokens,
    int MonthRequests,
    decimal? MonthCredits,
    DateTimeOffset MonthStartsAt,
    DateTimeOffset MonthEndsAt,
    IReadOnlyList<CostUsageDay> Days,
    string Source,
    string? Error)
{
    public CostUsageSnapshot? PreviousMonth { get; init; }

    public bool HasUsage => MonthRequests > 0 || MonthTokens > 0 || MonthCostUsd > 0;
}
