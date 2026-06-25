using System.IO;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Claude;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// Claude Code (Claude.ai Pro/Max) provider. Reads the rate-limit snapshot written by the
/// statusline helper (tools/claude-statusline-capture.ps1) into a local JSON file. Claude Code
/// only exposes rate_limits to its statusline, so a running Claude Code session is required to
/// keep the file fresh.
/// </summary>
public sealed class ClaudeCodeRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "claude";

    private const string Source = "statusline";
    private const string FiveHourName = "5 hour window";
    private const string WeeklyName = "Weekly window";

    public string Id => ProviderId;
    public string DisplayName => "Claude Code";

    public Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    private VendorRateLimitSnapshot Read()
    {
        var path = ClaudeCodePaths.RateLimitsFile;
        if (!File.Exists(path))
        {
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName,
                "Statusline helper not configured. See tools/claude-statusline-capture.ps1.",
                Source);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Claude Code rate-limits file could not be parsed");
            return VendorRateLimitSnapshot.Failed(
                Id, DisplayName, "Rate-limits file is unreadable.", Source);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var capturedAt = GetUnixTime(root, "captured_at");

            // The helper may write {"rate_limits": {...}} or the rate_limits object directly.
            var rateLimits = root.TryGetProperty("rate_limits", out var nested) &&
                             nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var buckets = new List<RateLimitBucket>();
            AddWindow(buckets, rateLimits, "five_hour", FiveHourName, TimeSpan.FromHours(5), capturedAt);
            AddWindow(buckets, rateLimits, "seven_day", WeeklyName, TimeSpan.FromDays(7), capturedAt);

            if (buckets.Count == 0)
            {
                return VendorRateLimitSnapshot.Failed(
                    Id, DisplayName,
                    "No rate-limit windows reported yet. Open a Claude Code session to populate them.",
                    Source);
            }

            return new VendorRateLimitSnapshot(
                VendorId: Id,
                DisplayName: DisplayName,
                Source: Source,
                PlanType: null,
                Buckets: buckets,
                CostUsage: null,
                ObservedAt: DateTimeOffset.Now,
                Error: null);
        }
    }

    private static void AddWindow(
        List<RateLimitBucket> buckets,
        JsonElement rateLimits,
        string key,
        string displayName,
        TimeSpan window,
        DateTimeOffset? capturedAt)
    {
        if (!rateLimits.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = GetDouble(element, "used_percentage");
        if (used is null)
        {
            return;
        }

        var resetAt = GetUnixTime(element, "resets_at");
        var now = DateTimeOffset.Now;

        // If the window has rolled over since capture, the stored percentage is stale; a freshly
        // reset window starts near zero, so report 0 with an explanatory note instead of a false high.
        if (resetAt is { } reset && reset <= now)
        {
            buckets.Add(new RateLimitBucket(
                Name: displayName,
                UsedPercent: 0.0,
                Window: window,
                ResetAt: null,
                Note: "Window reset since last Claude Code session; awaiting fresh data."));
            return;
        }

        string? note = null;
        if (capturedAt is { } captured && now - captured > TimeSpan.FromHours(1))
        {
            note = $"Data from last Claude Code session at {captured.LocalDateTime:g}.";
        }

        buckets.Add(new RateLimitBucket(
            Name: displayName,
            UsedPercent: Math.Clamp(used.Value, 0.0, 100.0),
            Window: window,
            ResetAt: resetAt,
            Note: note));
    }

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var d)
            ? d
            : null;

    private static DateTimeOffset? GetUnixTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
}
