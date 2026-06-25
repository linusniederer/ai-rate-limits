namespace AiRateLimits.Models;

/// <summary>
/// A single normalized rate-limit window for a provider.
/// <see cref="UsedPercent"/> drives health; progress bars visualize <see cref="RemainingPercent"/>.
/// </summary>
public sealed record RateLimitBucket(
    string Name,
    double UsedPercent,
    TimeSpan? Window,
    DateTimeOffset? ResetAt,
    bool AffectsHealth = true,
    string? Note = null,
    LimitHealth? MinimumHealth = null,
    string? ValueText = null,
    string? TabText = null)
{
    /// <summary>Available capacity remaining, clamped to 0..100.</summary>
    public double RemainingPercent => Math.Clamp(100.0 - UsedPercent, 0.0, 100.0);
}
