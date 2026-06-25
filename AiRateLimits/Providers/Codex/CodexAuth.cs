using System.IO;
using System.Text.Json;

namespace AiRateLimits.Providers.Codex;

/// <summary>
/// Access token and optional ChatGPT account id read from Codex auth.json.
/// </summary>
public sealed record CodexAuth(string AccessToken, string? AccountId)
{
    /// <summary>
    /// Reads auth.json. Accepts tokens.access_token / tokens.accountToken / accessToken variants and
    /// a top-level OPENAI_API_KEY fallback. Returns null when no usable token is found.
    /// </summary>
    public static CodexAuth? TryRead(string? authJsonPath = null)
    {
        var path = authJsonPath ?? CodexPaths.AuthJson;
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        string? token = null;
        string? accountId = null;

        if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            token = GetString(tokens, "access_token") ?? GetString(tokens, "accessToken");
            accountId = GetString(tokens, "account_id") ?? GetString(tokens, "accountId");
        }

        token ??= GetString(root, "OPENAI_API_KEY");
        accountId ??= GetString(root, "chatgpt_account_id");

        return string.IsNullOrWhiteSpace(token) ? null : new CodexAuth(token, accountId);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
