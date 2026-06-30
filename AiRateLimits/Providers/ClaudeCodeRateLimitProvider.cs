using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Claude;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// Claude Code (Claude.ai Pro/Max) provider. Reads the OAuth usage API, refreshing the access token
/// via the stored refresh token when needed (and writing it back in Claude Code's own format), then
/// falls back to the statusline helper's local file. Refreshing means no interactive Claude Code
/// session is required just to read usage.
/// </summary>
public sealed class ClaudeCodeRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "claude";

    private const string SourceApi = "api";
    private const string SourceStatusline = "statusline";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string RefreshUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string RefreshScope =
        "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private const string FiveHourName = "5 hour window";
    private const string WeeklyName = "Weekly window";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string Id => ProviderId;
    public string DisplayName => "Claude Code";

    public async Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        // 1) Live OAuth usage API, only while the existing token is still valid.
        try
        {
            var api = await TryReadApiAsync(cancellationToken).ConfigureAwait(false);
            if (api is not null)
            {
                return api;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude usage API read failed; falling back to statusline");
        }

        // 2) Statusline helper file.
        var statusline = ReadStatusline();
        if (statusline is not null)
        {
            return statusline;
        }

        return VendorRateLimitSnapshot.Failed(
            Id, DisplayName,
            "No Claude data. Run an interactive Claude Code session (refreshes the token / statusline).",
            SourceApi);
    }

    // ===================== OAuth usage API =====================

    private async Task<VendorRateLimitSnapshot?> TryReadApiAsync(CancellationToken cancellationToken)
    {
        var creds = ClaudeOAuth.TryRead();
        if (creds is null)
        {
            return null;
        }

        // Refresh proactively if the token is already expired and we can.
        if (creds.IsExpired && creds.CanRefresh)
        {
            creds = await TryRefreshAsync(creds, cancellationToken).ConfigureAwait(false) ?? creds;
        }

        var json = await SendUsageAsync(creds.AccessToken, cancellationToken).ConfigureAwait(false);

        // A 401 (json == null) on a token we did not just refresh: refresh once and retry.
        if (json is null && creds.CanRefresh)
        {
            var refreshed = await TryRefreshAsync(creds, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null)
            {
                json = await SendUsageAsync(refreshed.AccessToken, cancellationToken).ConfigureAwait(false);
            }
        }

        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var container = FindRateLimitsContainer(doc.RootElement);
        var buckets = new List<RateLimitBucket>();
        AddApiWindow(buckets, container, FiveHourName, TimeSpan.FromHours(5), "five_hour", "5h");
        AddApiWindow(buckets, container, WeeklyName, TimeSpan.FromDays(7), "seven_day", "7d");
        AddExtraUsage(buckets, doc.RootElement);

        if (buckets.Count == 0)
        {
            Log.Warning("Claude usage API response had no recognizable windows: {Json}", Truncate(json));
            return null;
        }

        return new VendorRateLimitSnapshot(
            Id, DisplayName, SourceApi, PlanType: null, buckets, CostUsage: null,
            ObservedAt: DateTimeOffset.Now, Error: null);
    }

    /// <summary>Calls the OAuth usage endpoint; returns the body on success, null on any failure.</summary>
    private static async Task<string?> SendUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.0");

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Claude usage API returned {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges the refresh token for a fresh access token and persists the rotated tokens back into
    /// Claude Code's credentials file. Returns the refreshed credentials, or null on failure.
    /// </summary>
    private static async Task<ClaudeOAuth?> TryRefreshAsync(ClaudeOAuth creds, CancellationToken cancellationToken)
    {
        if (!creds.CanRefresh)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = creds.RefreshToken,
                client_id = OAuthClientId,
                scope = RefreshScope
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Claude token refresh returned {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var newAccess = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(newAccess))
            {
                return null;
            }

            var newRefresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            DateTimeOffset? newExpires = root.TryGetProperty("expires_in", out var e) &&
                                         e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var secs)
                ? DateTimeOffset.UtcNow.AddSeconds(secs)
                : null;

            return creds.WithRefreshed(newAccess!, newRefresh, newExpires);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude token refresh failed");
            return null;
        }
    }

    /// <summary>
    /// Adds an informational "Usage credits" bucket from extra_usage, but only when it is enabled.
    /// Never affects health (it is overflow spend, not a plan limit).
    /// </summary>
    private static void AddExtraUsage(List<RateLimitBucket> buckets, JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var ex) || ex.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (GetBool(ex, "is_enabled") != true)
        {
            return;
        }

        var utilization = FirstDouble(ex, "utilization") ?? 0.0;
        var decimals = (int)(FirstDouble(ex, "decimal_places") ?? 0);
        var divisor = Math.Pow(10, decimals);
        var used = FirstDouble(ex, "used_credits");
        var limit = FirstDouble(ex, "monthly_limit");
        var currency = GetString(ex, "currency");

        string? valueText = null;
        if (used is { } u && limit is { } l && l > 0)
        {
            var suffix = string.IsNullOrWhiteSpace(currency) ? "" : $" {currency}";
            valueText = $"{u / divisor:0.##} / {l / divisor:0.##}{suffix} used";
        }

        buckets.Add(new RateLimitBucket(
            "Usage credits",
            Math.Clamp(utilization, 0.0, 100.0),
            Window: null,
            ResetAt: null,
            AffectsHealth: false,
            Note: "Extra usage credits — cover you beyond plan limits.",
            ValueText: valueText));
    }

    private static JsonElement FindRateLimitsContainer(JsonElement root)
    {
        if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            return rl;
        }
        return root;
    }

    /// <summary>Defensively pulls a window by any of its candidate keys and percent/reset field names.</summary>
    private static void AddApiWindow(
        List<RateLimitBucket> buckets, JsonElement container, string displayName, TimeSpan window,
        params string[] keyCandidates)
    {
        JsonElement win = default;
        var found = false;
        foreach (var key in keyCandidates)
        {
            if (container.TryGetProperty(key, out win) && win.ValueKind == JsonValueKind.Object)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            return;
        }

        var used = FirstDouble(win, "used_percentage", "utilization", "used_percent", "percent");
        if (used is null)
        {
            return;
        }

        var resetAt = FirstReset(win, "resets_at", "reset_at", "resets", "reset");
        AddWindowBucket(buckets, displayName, window, used.Value, resetAt, capturedAt: DateTimeOffset.Now);
    }

    // ===================== Statusline file =====================

    private VendorRateLimitSnapshot? ReadStatusline()
    {
        var path = ClaudeCodePaths.RateLimitsFile;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var capturedAt = FirstReset(root, "captured_at") ?? DateTimeOffset.Now;

            var rateLimits = root.TryGetProperty("rate_limits", out var nested) &&
                             nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var buckets = new List<RateLimitBucket>();
            AddStatuslineWindow(buckets, rateLimits, "five_hour", FiveHourName, TimeSpan.FromHours(5), capturedAt);
            AddStatuslineWindow(buckets, rateLimits, "seven_day", WeeklyName, TimeSpan.FromDays(7), capturedAt);

            if (buckets.Count == 0)
            {
                return null;
            }

            return new VendorRateLimitSnapshot(
                Id, DisplayName, SourceStatusline, PlanType: null, buckets, CostUsage: null,
                ObservedAt: DateTimeOffset.Now, Error: null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude statusline file could not be parsed");
            return null;
        }
    }

    private static void AddStatuslineWindow(
        List<RateLimitBucket> buckets, JsonElement rateLimits, string key, string displayName,
        TimeSpan window, DateTimeOffset capturedAt)
    {
        if (!rateLimits.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = FirstDouble(element, "used_percentage", "utilization");
        if (used is null)
        {
            return;
        }

        var resetAt = FirstReset(element, "resets_at", "reset_at");
        AddWindowBucket(buckets, displayName, window, used.Value, resetAt, capturedAt);
    }

    // ===================== Shared bucket building =====================

    private static void AddWindowBucket(
        List<RateLimitBucket> buckets, string displayName, TimeSpan window,
        double usedPercent, DateTimeOffset? resetAt, DateTimeOffset capturedAt)
    {
        var now = DateTimeOffset.Now;

        // A rolled-over window's stored percentage is stale; report 0 with a note rather than a false high.
        if (resetAt is { } reset && reset <= now)
        {
            buckets.Add(new RateLimitBucket(
                displayName, 0.0, window, ResetAt: null,
                Note: "Window reset since last reading; awaiting fresh data."));
            return;
        }

        string? note = null;
        if (now - capturedAt > TimeSpan.FromHours(1))
        {
            note = $"Data from last Claude Code session at {capturedAt.LocalDateTime:g}.";
        }

        buckets.Add(new RateLimitBucket(
            displayName, Math.Clamp(usedPercent, 0.0, 100.0), window, resetAt, Note: note));
    }

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

    private static double? FirstDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var d))
            {
                return d;
            }
        }
        return null;
    }

    private static DateTimeOffset? FirstReset(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var num) && num > 0)
            {
                // Heuristic: large values are milliseconds, otherwise seconds.
                return num > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(num)
                    : DateTimeOffset.FromUnixTimeSeconds(num);
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400];
}
