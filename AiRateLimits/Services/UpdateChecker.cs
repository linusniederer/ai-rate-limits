using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace AiRateLimits.Services;

/// <summary>
/// Checks the GitHub releases API for a newer stable release than the running build.
/// Pre-releases (e.g. nightly) are ignored by the "latest" endpoint.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestUrl =
        "https://api.github.com/repos/linusniederer/ai-rate-limits/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record Result(string LatestVersion, string Url);

    /// <summary>Returns update info when a newer version is available, otherwise null.</summary>
    public async Task<Result?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.UserAgent.ParseAdd("AiRateLimits");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var latest = ParseVersion(tag);
            var current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
            if (latest is null || current is null || latest <= current)
            {
                return null;
            }

            return new Result(tag, url ?? "https://github.com/linusniederer/ai-rate-limits/releases");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            return null;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var v) ? Normalize(v) : null;
    }

    private static Version? Normalize(Version? v) =>
        v is null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
