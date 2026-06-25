namespace AiRateLimits.Models;

/// <summary>
/// Normalized health state for a bucket, provider, or the aggregate UI summary.
/// Ordered so a numeric comparison can pick the "worst" state.
/// </summary>
public enum LimitHealth
{
    Unknown,
    Healthy,
    Warning,
    Critical
}
