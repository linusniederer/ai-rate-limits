using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Copilot;
using AiRateLimits.Services;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// GitHub Copilot provider. Reads usage from the internal copilot_internal/user endpoint using a
/// GitHub OAuth token obtained via device flow (see <see cref="CopilotLogin"/>) and stored in
/// Windows Credential Manager. The endpoint is internal and can change.
/// </summary>
public sealed class CopilotRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "copilot";

    private const string Source = "live";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly SettingsStore _settingsStore;

    public CopilotRateLimitProvider(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public string Id => ProviderId;
    public string DisplayName => "GitHub Copilot";

    public async Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var credential = WindowsCredential.Read(CopilotHosts.CredentialTarget);
        if (credential is null || string.IsNullOrWhiteSpace(credential.Secret))
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName, "Not logged in. Use the tray menu: GitHub Copilot Login.", Source);
        }

        var enterpriseHost = _settingsStore.Load().CopilotEnterpriseHost;
        var apiHost = CopilotHosts.ApiHost(enterpriseHost);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://{apiHost}/copilot_internal/user");
            request.Headers.TryAddWithoutValidation("Authorization", $"token {credential.Secret}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
            request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return VendorRateLimitSnapshot.Failed(
                    Id, DisplayName, $"Copilot usage endpoint returned {(int)response.StatusCode}.", Source);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return BuildSnapshot(doc.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Copilot usage read failed");
            return VendorRateLimitSnapshot.Failed(Id, DisplayName, ex.Message, Source);
        }
    }

    private VendorRateLimitSnapshot BuildSnapshot(JsonElement root)
    {
        var resetAt = ParseResetDate(root, "quota_reset_date");
        var buckets = new List<RateLimitBucket>();

        var snapshots = GetObject(root, "quota_snapshots") ?? GetObject(root, "monthly_quotas")
            ?? GetObject(root, "limited_user_quotas");

        if (snapshots is { } s)
        {
            AddBuckets(buckets, s, "premium_interactions", "Premium requests", resetAt);
            AddBuckets(buckets, s, "chat", "Chat", resetAt);
        }

        if (buckets.Count == 0)
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName, "No Copilot quota snapshots reported.", Source);
        }

        return new VendorRateLimitSnapshot(
            VendorId: Id,
            DisplayName: DisplayName,
            Source: Source,
            PlanType: GetString(root, "copilot_plan"),
            Buckets: buckets,
            CostUsage: null,
            ObservedAt: DateTimeOffset.Now,
            Error: null);
    }

    private static void AddBuckets(
        List<RateLimitBucket> buckets,
        JsonElement snapshots,
        string key,
        string displayName,
        DateTimeOffset? resetAt)
    {
        if (!snapshots.TryGetProperty(key, out var snap) || snap.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var unlimited = GetBool(snap, "unlimited") ?? false;
        var remainingPercent = ResolveRemainingPercent(snap, unlimited);
        var usedPercent = Math.Clamp(100.0 - remainingPercent, 0.0, 100.0);
        var includedExhausted = remainingPercent <= 0.0 && !unlimited;

        var overagePermitted = GetBool(snap, "overage_permitted") ?? false;
        var overageCount = GetLong(snap, "overage_count");

        buckets.Add(new RateLimitBucket(
            Name: displayName,
            UsedPercent: unlimited ? 0.0 : usedPercent,
            Window: null,
            ResetAt: resetAt,
            AffectsHealth: !(overagePermitted && includedExhausted),
            Note: unlimited ? "Unlimited" : null));

        if (overagePermitted)
        {
            var active = includedExhausted;
            var valueText = overageCount is { } c ? $"{c} top-up requests used" : "Top-up requests enabled";
            var tabText = overageCount is { } tc ? $"{tc} top-up" : "top-up";

            buckets.Add(new RateLimitBucket(
                Name: $"{displayName} top-up",
                UsedPercent: 0.0,
                Window: null,
                ResetAt: resetAt,
                AffectsHealth: active,
                Note: active
                    ? "Included quota exhausted; top-up requests are active."
                    : "Top-up requests: informational until included quota is exhausted.",
                MinimumHealth: active ? LimitHealth.Warning : null,
                ValueText: valueText,
                TabText: tabText));
        }
    }

    private static double ResolveRemainingPercent(JsonElement snap, bool unlimited)
    {
        if (unlimited)
        {
            return 100.0;
        }

        if (GetDouble(snap, "percent_remaining") is { } pct)
        {
            return Math.Clamp(pct, 0.0, 100.0);
        }

        var remaining = GetDouble(snap, "remaining");
        var entitlement = GetDouble(snap, "entitlement");
        if (remaining is { } r && entitlement is { } e && e > 0)
        {
            return Math.Clamp(r / e * 100.0, 0.0, 100.0);
        }

        return 100.0;
    }

    private static DateTimeOffset? ParseResetDate(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
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

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
