namespace AiRateLimits.Models;

/// <summary>
/// A provider's full reading at a point in time. Providers return this even on failure
/// (with <see cref="Error"/> set) rather than throwing.
/// </summary>
public sealed record VendorRateLimitSnapshot(
    string VendorId,
    string DisplayName,
    string Source,
    string? PlanType,
    IReadOnlyList<RateLimitBucket> Buckets,
    CostUsageSnapshot? CostUsage,
    DateTimeOffset ObservedAt,
    string? Error,
    AccountCreditsSnapshot? AccountCredits = null)
{
    /// <summary>Convenience factory for a failed read with no bucket data.</summary>
    public static VendorRateLimitSnapshot Failed(
        string vendorId,
        string displayName,
        string error,
        string source = "live") =>
        new(vendorId, displayName, source, PlanType: null,
            Buckets: Array.Empty<RateLimitBucket>(),
            CostUsage: null,
            ObservedAt: DateTimeOffset.Now,
            Error: error);
}
