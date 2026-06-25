using AiRateLimits.Models;

namespace AiRateLimits.Services;

/// <summary>
/// Computes health from buckets. Only <see cref="RateLimitBucket.AffectsHealth"/> buckets count.
/// A bucket's <see cref="RateLimitBucket.MinimumHealth"/> floors its evaluated health.
/// </summary>
public static class HealthCalculator
{
    public sealed record HealthResult(LimitHealth Health, RateLimitBucket? WorstBucket);

    /// <summary>Health for the aggregate UI summary across all enabled provider snapshots.</summary>
    public static HealthResult Aggregate(
        IEnumerable<VendorRateLimitSnapshot> snapshots,
        int warningPercent,
        int criticalPercent)
    {
        var buckets = snapshots.SelectMany(s => s.Buckets);
        return Evaluate(buckets, warningPercent, criticalPercent);
    }

    /// <summary>Health for the selected status provider only; Unknown when it has no snapshot.</summary>
    public static HealthResult ForStatusProvider(
        VendorRateLimitSnapshot? statusSnapshot,
        int warningPercent,
        int criticalPercent)
    {
        if (statusSnapshot is null)
        {
            return new HealthResult(LimitHealth.Unknown, null);
        }

        return Evaluate(statusSnapshot.Buckets, warningPercent, criticalPercent);
    }

    public static HealthResult Evaluate(
        IEnumerable<RateLimitBucket> buckets,
        int warningPercent,
        int criticalPercent)
    {
        LimitHealth worst = LimitHealth.Unknown;
        RateLimitBucket? worstBucket = null;
        var any = false;

        foreach (var bucket in buckets)
        {
            if (!bucket.AffectsHealth)
            {
                continue;
            }

            any = true;
            var health = ClassifyBucket(bucket, warningPercent, criticalPercent);
            if (worstBucket is null || health > worst)
            {
                worst = health;
                worstBucket = bucket;
            }
        }

        if (!any)
        {
            return new HealthResult(LimitHealth.Unknown, null);
        }

        return new HealthResult(worst, worstBucket);
    }

    /// <summary>Classifies one bucket, raising it to at least its <see cref="RateLimitBucket.MinimumHealth"/>.</summary>
    public static LimitHealth ClassifyBucket(RateLimitBucket bucket, int warningPercent, int criticalPercent)
    {
        LimitHealth health;
        if (bucket.UsedPercent >= criticalPercent)
        {
            health = LimitHealth.Critical;
        }
        else if (bucket.UsedPercent >= warningPercent)
        {
            health = LimitHealth.Warning;
        }
        else
        {
            health = LimitHealth.Healthy;
        }

        if (bucket.MinimumHealth is { } floor && floor > health)
        {
            health = floor;
        }

        return health;
    }
}
