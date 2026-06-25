namespace AiRateLimits.Models;

/// <summary>
/// Informational account/top-up credit balance. Never affects health, tray color, or notifications.
/// </summary>
public sealed record AccountCreditsSnapshot(
    decimal? Remaining,
    bool HasCredits,
    bool Unlimited,
    string Source,
    long? ApproxCloudMessages = null,
    long? ApproxLocalMessages = null,
    bool OverageLimitReached = false);
