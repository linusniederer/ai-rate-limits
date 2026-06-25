using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using AiRateLimits.Models;
using AiRateLimits.Providers.JetBrains;
using AiRateLimits.Services;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// JetBrains AI provider. Reads local Rider quota state from
/// &lt;IDE base path&gt;\options\AIAssistantQuotaManager2.xml. This is local IDE state, not a
/// public API; the XML/embedded-JSON shape can change.
/// </summary>
public sealed class JetBrainsRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "jetbrains";

    private const string Source = "live";
    private const string ComponentName = "AIAssistantQuotaManager2";
    private const string MonthlyBucketName = "Monthly credits";
    private const string TopUpBucketName = "Top-up credits";

    private const string TopUpInformationalNote =
        "Top-up credits: informational until included credits are exhausted.";
    private const string TopUpActiveNote =
        "Included credits are exhausted; top-up credits are active.";

    private readonly SettingsStore _settingsStore;

    public JetBrainsRateLimitProvider(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public string Id => ProviderId;
    public string DisplayName => "JetBrains AI";

    public Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return Task.FromResult(Read());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "JetBrains AI read failed");
            return Task.FromResult(VendorRateLimitSnapshot.Failed(Id, DisplayName, ex.Message, Source));
        }
    }

    private VendorRateLimitSnapshot Read()
    {
        var basePath = _settingsStore.Load().JetBrainsIdeBasePath;
        var quotaFile = JetBrainsDiscovery.FindQuotaFile(basePath);
        if (quotaFile is null)
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName,
                "No Rider AI quota file found. Open JetBrains AI Assistant first, or set a manual path.",
                Source);
        }

        var (quotaInfo, nextRefill) = ReadComponentOptions(quotaFile);
        if (quotaInfo is null)
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName, "Quota file did not contain quotaInfo.", Source);
        }

        using var quotaDoc = JsonDocument.Parse(quotaInfo);
        var quota = quotaDoc.RootElement;

        var resetAt = ParseReset(nextRefill, quota);
        var buckets = BuildBuckets(quota, resetAt);
        if (buckets.Count == 0)
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName, "Quota file did not contain a usable tariff quota.", Source);
        }

        return new VendorRateLimitSnapshot(
            VendorId: Id,
            DisplayName: DisplayName,
            Source: Source,
            PlanType: GetString(quota, "type"),
            Buckets: buckets,
            CostUsage: null,
            ObservedAt: DateTimeOffset.Now,
            Error: null);
    }

    private static (string? QuotaInfo, string? NextRefill) ReadComponentOptions(string quotaFile)
    {
        var xdoc = XDocument.Load(quotaFile);
        var component = xdoc
            .Descendants("component")
            .FirstOrDefault(c => (string?)c.Attribute("name") == ComponentName);

        if (component is null)
        {
            return (null, null);
        }

        string? OptionValue(string name) => component
            .Elements("option")
            .FirstOrDefault(o => (string?)o.Attribute("name") == name)
            ?.Attribute("value")?.Value;

        return (OptionValue("quotaInfo"), OptionValue("nextRefill"));
    }

    private static List<RateLimitBucket> BuildBuckets(JsonElement quota, DateTimeOffset? resetAt)
    {
        var buckets = new List<RateLimitBucket>();

        var tariff = GetObject(quota, "tariffQuota");
        var topUp = GetObject(quota, "topUpQuota");

        // Monthly (tariff) bucket. Missing/zero maximum is treated as exhausted.
        double monthlyUsed;
        if (tariff is { } t && GetDouble(t, "maximum") is { } max && max > 0)
        {
            monthlyUsed = GetDouble(t, "available") is { } avail
                ? Math.Clamp(100.0 - avail / max * 100.0, 0.0, 100.0)
                : Math.Clamp((GetDouble(t, "current") ?? max) / max * 100.0, 0.0, 100.0);
        }
        else
        {
            monthlyUsed = 100.0;
        }

        var monthlyExhausted = monthlyUsed >= 100.0;
        var hasActiveTopUp = topUp is not null && monthlyExhausted;

        buckets.Add(new RateLimitBucket(
            Name: MonthlyBucketName,
            UsedPercent: monthlyUsed,
            Window: null,
            ResetAt: resetAt,
            AffectsHealth: !hasActiveTopUp,
            Note: topUp is null ? null : (hasActiveTopUp ? TopUpActiveNote : TopUpInformationalNote)));

        if (topUp is { } u)
        {
            double topUpUsed;
            if (GetDouble(u, "maximum") is { } tuMax && tuMax > 0)
            {
                topUpUsed = GetDouble(u, "available") is { } tuAvail
                    ? Math.Clamp(100.0 - tuAvail / tuMax * 100.0, 0.0, 100.0)
                    : Math.Clamp((GetDouble(u, "current") ?? 0) / tuMax * 100.0, 0.0, 100.0);
            }
            else
            {
                // Zero maximum here is "no top-up configured", not real exhaustion.
                topUpUsed = 0.0;
            }

            buckets.Add(new RateLimitBucket(
                Name: TopUpBucketName,
                UsedPercent: topUpUsed,
                Window: null,
                ResetAt: null,
                AffectsHealth: hasActiveTopUp,
                Note: hasActiveTopUp ? TopUpActiveNote : TopUpInformationalNote,
                MinimumHealth: hasActiveTopUp ? LimitHealth.Warning : null));
        }

        return buckets;
    }

    private static DateTimeOffset? ParseReset(string? nextRefill, JsonElement quota)
    {
        if (!string.IsNullOrWhiteSpace(nextRefill))
        {
            try
            {
                using var refillDoc = JsonDocument.Parse(nextRefill);
                if (ParseDate(refillDoc.RootElement, "next") is { } next)
                {
                    return next;
                }
            }
            catch
            {
                // Fall through to quotaInfo.until.
            }
        }

        return ParseDate(quota, "until");
    }

    private static DateTimeOffset? ParseDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) && epoch > 0)
        {
            // Heuristic: values beyond ~year 5138 in seconds are milliseconds.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var d)
            ? d
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
