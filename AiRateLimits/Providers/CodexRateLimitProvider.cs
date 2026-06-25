using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Codex;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// Codex provider. Primary implemented provider. Tries the live wham/usage endpoint first,
/// then falls back to the newest cached codex.rate_limits event in logs_2.sqlite.
/// TODO: local cost scanner + models.dev pricing cache (CostUsage stays null for now).
/// </summary>
public sealed class CodexRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "codex";

    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string SourceLive = "live";
    private const string SourceCached = "cached log";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string Id => ProviderId;
    public string DisplayName => "Codex";

    public async Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var live = await TryReadLiveAsync(cancellationToken).ConfigureAwait(false);
            if (live is not null)
            {
                return live;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex live usage read failed; trying cached fallback");
        }

        try
        {
            var cached = TryReadCached();
            if (cached is not null)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex cached fallback read failed");
        }

        return VendorRateLimitSnapshot.Failed(
            Id, DisplayName, "No live or cached Codex usage data available.");
    }

    private async Task<VendorRateLimitSnapshot?> TryReadLiveAsync(CancellationToken cancellationToken)
    {
        var auth = CodexAuth.TryRead();
        if (auth is null)
        {
            Log.Information("Codex auth.json not found or has no usable token");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        }

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Codex usage endpoint returned {Status}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var buckets = CodexUsageParser.ParseBuckets(root);
        if (buckets.Count == 0)
        {
            Log.Warning("Codex live response contained no recognizable rate-limit buckets");
            return null;
        }

        return new VendorRateLimitSnapshot(
            VendorId: Id,
            DisplayName: DisplayName,
            Source: SourceLive,
            PlanType: CodexUsageParser.ParsePlanType(root),
            Buckets: buckets,
            CostUsage: null,
            ObservedAt: DateTimeOffset.Now,
            Error: null,
            AccountCredits: CodexUsageParser.ParseCredits(root, SourceLive));
    }

    private VendorRateLimitSnapshot? TryReadCached()
    {
        var body = CodexSqliteFallback.TryReadLatestBody();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var buckets = CodexUsageParser.ParseBuckets(root);
        if (buckets.Count == 0)
        {
            return null;
        }

        return new VendorRateLimitSnapshot(
            VendorId: Id,
            DisplayName: DisplayName,
            Source: SourceCached,
            PlanType: CodexUsageParser.ParsePlanType(root),
            Buckets: buckets,
            CostUsage: null,
            ObservedAt: DateTimeOffset.Now,
            Error: null,
            AccountCredits: CodexUsageParser.ParseCredits(root, SourceCached));
    }
}
