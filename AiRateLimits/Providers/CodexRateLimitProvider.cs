using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AiRateLimits.Models;
using AiRateLimits.Providers.Codex;
using AiRateLimits.Providers.Codex.Cost;
using Serilog;

namespace AiRateLimits.Providers;

/// <summary>
/// Codex provider. Primary implemented provider. Tries the live wham/usage endpoint first,
/// then falls back to the newest cached codex.rate_limits event in logs_2.sqlite. Attaches an
/// informational monthly cost estimate scanned from local session logs.
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

    // Cost scanning is expensive (multi-MB logs); recompute at most this often and reuse the cache
    // during fast reset-watch polls.
    private static readonly TimeSpan CostRecomputeInterval = TimeSpan.FromMinutes(3);
    private static readonly CodexCostScanner CostScanner = new(new CodexModelPricing());
    private static readonly SemaphoreSlim CostLock = new(1, 1);
    private static CostUsageSnapshot? _cachedCost;
    private static DateTimeOffset _costComputedAt = DateTimeOffset.MinValue;

    public string Id => ProviderId;
    public string DisplayName => "Codex";

    public async Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot.Buckets.Count > 0)
        {
            var cost = await GetCostAsync(cancellationToken).ConfigureAwait(false);
            if (cost is not null)
            {
                snapshot = snapshot with { CostUsage = cost };
            }
        }

        return snapshot;
    }

    private async Task<VendorRateLimitSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken)
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

    private static async Task<CostUsageSnapshot?> GetCostAsync(CancellationToken cancellationToken)
    {
        if (_cachedCost is not null && DateTimeOffset.Now - _costComputedAt < CostRecomputeInterval)
        {
            return _cachedCost;
        }

        // If a scan is already running, reuse the cached value rather than queuing another.
        if (!await CostLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _cachedCost;
        }

        try
        {
            if (_cachedCost is not null && DateTimeOffset.Now - _costComputedAt < CostRecomputeInterval)
            {
                return _cachedCost;
            }

            _cachedCost = await CostScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            _costComputedAt = DateTimeOffset.Now;
            return _cachedCost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex cost scan failed");
            return _cachedCost;
        }
        finally
        {
            CostLock.Release();
        }
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
