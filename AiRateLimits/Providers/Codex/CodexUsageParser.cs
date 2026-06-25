using System.Text.Json;
using AiRateLimits.Models;

namespace AiRateLimits.Providers.Codex;

/// <summary>
/// Parses a Codex usage payload (live API response or a cached codex.rate_limits event) into
/// normalized buckets, plan type, and optional account credits. The payload shapes are
/// reverse-engineered and unstable, so every lookup is defensive.
/// </summary>
public static class CodexUsageParser
{
    public const string PrimaryBucketName = "5 hour window";
    public const string SecondaryBucketName = "Weekly window";

    public static string? ParsePlanType(JsonElement root) =>
        GetString(root, "plan_type") ?? GetString(root, "planType");

    public static IReadOnlyList<RateLimitBucket> ParseBuckets(JsonElement root)
    {
        var buckets = new List<RateLimitBucket>();

        var container = FindRateLimitContainer(root);
        if (container is { } c)
        {
            if (FindWindow(c, "primary", "primary_window") is { } primary)
            {
                AddBucket(buckets, PrimaryBucketName, primary);
            }

            if (FindWindow(c, "secondary", "secondary_window") is { } secondary)
            {
                AddBucket(buckets, SecondaryBucketName, secondary);
            }
        }

        if (root.TryGetProperty("additional_rate_limits", out var additional) &&
            additional.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in additional.EnumerateArray())
            {
                index++;
                var name = GetString(item, "name") ?? GetString(item, "label") ?? $"Additional limit {index}";
                AddBucket(buckets, name, item);
            }
        }

        return buckets;
    }

    public static AccountCreditsSnapshot? ParseCredits(JsonElement root, string source)
    {
        if (!root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var remaining = FindCreditBalance(credits);
        var hasCredits = GetBool(credits, "has_credits") ?? false;
        var unlimited = GetBool(credits, "unlimited") ?? false;
        var overageReached = GetBool(credits, "overage_limit_reached") ?? false;
        var cloud = GetLong(credits, "approx_cloud_messages");
        var local = GetLong(credits, "approx_local_messages");

        return new AccountCreditsSnapshot(
            Remaining: remaining,
            HasCredits: hasCredits,
            Unlimited: unlimited,
            Source: source,
            ApproxCloudMessages: cloud,
            ApproxLocalMessages: local,
            OverageLimitReached: overageReached);
    }

    private static JsonElement? FindRateLimitContainer(JsonElement root)
    {
        if (root.TryGetProperty("rate_limit", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            return single;
        }

        if (root.TryGetProperty("rate_limits", out var plural) && plural.ValueKind == JsonValueKind.Object)
        {
            return plural;
        }

        return null;
    }

    private static JsonElement? FindWindow(JsonElement container, string primaryKey, string altKey)
    {
        if (container.TryGetProperty(primaryKey, out var window) && window.ValueKind == JsonValueKind.Object)
        {
            return window;
        }

        if (container.TryGetProperty(altKey, out var alt) && alt.ValueKind == JsonValueKind.Object)
        {
            return alt;
        }

        return null;
    }

    private static void AddBucket(List<RateLimitBucket> buckets, string name, JsonElement window)
    {
        var used = GetDouble(window, "used_percent");
        if (used is null)
        {
            return;
        }

        buckets.Add(new RateLimitBucket(
            Name: name,
            UsedPercent: Math.Clamp(used.Value, 0.0, 100.0),
            Window: ParseWindow(window),
            ResetAt: ParseResetAt(window)));
    }

    private static TimeSpan? ParseWindow(JsonElement window)
    {
        if (GetDouble(window, "window_minutes") is { } minutes && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (GetDouble(window, "limit_window_seconds") is { } seconds && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static DateTimeOffset? ParseResetAt(JsonElement window)
    {
        if (GetLong(window, "reset_at") is { } unix && unix > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    private static decimal? FindCreditBalance(JsonElement credits)
    {
        string[] knownKeys =
        {
            "balance", "remaining", "credits_remaining", "creditsRemaining",
            "remaining_credits", "remainingCredits", "credit_balance", "creditBalance"
        };

        foreach (var key in knownKeys)
        {
            if (credits.TryGetProperty(key, out var value) && TryGetDecimal(value, out var balance))
            {
                return balance;
            }
        }

        // Fallback: any non-message numeric property whose name hints at balance/remaining.
        foreach (var property in credits.EnumerateObject())
        {
            var lower = property.Name.ToLowerInvariant();
            if (lower.Contains("message"))
            {
                continue;
            }

            if ((lower.Contains("balance") || lower.Contains("remaining")) &&
                TryGetDecimal(property.Value, out var balance))
            {
                return balance;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var d)
            ? d
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var l)
            ? l
            : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out result))
        {
            return true;
        }

        result = 0m;
        return false;
    }
}
