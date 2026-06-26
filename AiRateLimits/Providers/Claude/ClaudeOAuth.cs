using System.IO;
using System.Text.Json;

namespace AiRateLimits.Providers.Claude;

/// <summary>
/// Reads the Claude Code OAuth access token from ~/.claude/.credentials.json. Read-only: this app
/// never refreshes or writes the token back, to avoid disturbing Claude Code's own auth.
/// </summary>
public sealed record ClaudeOAuth(string AccessToken, DateTimeOffset? ExpiresAt)
{
    public bool IsExpired => ExpiresAt is { } e && DateTimeOffset.UtcNow >= e;

    public static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static ClaudeOAuth? TryRead()
    {
        var path = CredentialsPath;
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

        var token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number &&
            e.TryGetInt64(out var ms) && ms > 0)
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return new ClaudeOAuth(token, expires);
    }
}
