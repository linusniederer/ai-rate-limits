using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Claude;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// Claude Code (Claude.ai Pro/Max) provider. Tries the OAuth usage API first when a valid token
/// is present, then falls back to the statusline helper's local file. The token is read-only — this
/// app never refreshes or rewrites Claude Code's credentials.
/// </summary>
public sealed class ClaudeCodeRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "claude";

    private const string SourceApi = "api";
    private const string SourceStatusline = "statusline";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
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
        var auth = ClaudeOAuth.TryRead();
        if (auth is null || auth.IsExpired)
        {
            // Refreshing would require the rotating refresh token and could break Claude Code's
            // login, so we deliberately skip the API when the token is missing or expired.
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Claude usage API returned {Status}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var container = FindRateLimitsContainer(doc.RootElement);
        var buckets = new List<RateLimitBucket>();
        AddApiWindow(buckets, container, FiveHourName, TimeSpan.FromHours(5), "five_hour", "5h");
        AddApiWindow(buckets, container, WeeklyName, TimeSpan.FromDays(7), "seven_day", "7d");

        if (buckets.Count == 0)
        {
            Log.Warning("Claude usage API response had no recognizable windows: {Json}", Truncate(json));
            return null;
        }

        return new VendorRateLimitSnapshot(
            Id, DisplayName, SourceApi, PlanType: null, buckets, CostUsage: null,
            ObservedAt: DateTimeOffset.Now, Error: null);
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
