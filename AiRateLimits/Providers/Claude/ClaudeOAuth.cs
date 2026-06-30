using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiRateLimits.Providers.Claude;

/// <summary>
/// Claude Code OAuth credentials from ~/.claude/.credentials.json. Supports refreshing the access
/// token via the rotating refresh token and writing it back in Claude Code's own format, so a
/// refresh never breaks Claude Code's login.
/// </summary>
public sealed record ClaudeOAuth(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string CredentialsPath)
{
    // Treat a token expiring within the next minute as already expired.
    public bool IsExpired => ExpiresAt is { } e && DateTimeOffset.UtcNow >= e.AddMinutes(-1);

    public bool CanRefresh => !string.IsNullOrWhiteSpace(RefreshToken);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static ClaudeOAuth? TryRead(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            oauth.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var token = GetString(oauth, "accessToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var refresh = GetString(oauth, "refreshToken");

        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number &&
            e.TryGetInt64(out var ms) && ms > 0)
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return new ClaudeOAuth(token!, refresh, expires, path);
    }

    /// <summary>
    /// Writes the refreshed tokens back into the existing credentials file, preserving all other
    /// fields and Claude Code's structure (claudeAiOauth.{accessToken,refreshToken,expiresAt}).
    /// </summary>
    public ClaudeOAuth WithRefreshed(string newAccessToken, string? newRefreshToken, DateTimeOffset? newExpiresAt)
    {
        try
        {
            if (File.Exists(CredentialsPath) &&
                JsonNode.Parse(File.ReadAllText(CredentialsPath)) is JsonObject root)
            {
                var oauth = root["claudeAiOauth"] as JsonObject ?? new JsonObject();
                root["claudeAiOauth"] = oauth;
                oauth["accessToken"] = newAccessToken;
                if (!string.IsNullOrWhiteSpace(newRefreshToken))
                {
                    oauth["refreshToken"] = newRefreshToken;
                }
                if (newExpiresAt is { } exp)
                {
                    oauth["expiresAt"] = exp.ToUnixTimeMilliseconds();
                }

                File.WriteAllText(CredentialsPath, root.ToJsonString());
            }
        }
        catch
        {
            // If we cannot persist, still return the in-memory refreshed credentials for this run.
        }

        return this with
        {
            AccessToken = newAccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(newRefreshToken) ? RefreshToken : newRefreshToken,
            ExpiresAt = newExpiresAt ?? ExpiresAt
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
