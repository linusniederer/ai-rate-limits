namespace AiRateLimits.Providers.Copilot;

/// <summary>
/// Resolves GitHub web and API hosts, supporting GitHub Enterprise via a configured host.
/// </summary>
public static class CopilotHosts
{
    public const string CredentialTarget = "AiRateLimits.Copilot.GitHubOAuth";
    public const string OAuthClientId = "Iv1.b507a08c87ecfe98";
    public const string Scopes = "read:user";

    /// <summary>The GitHub web host: github.com by default, or the normalized configured host.</summary>
    public static string WebHost(string? configuredHost)
    {
        var host = Normalize(configuredHost);
        return string.IsNullOrEmpty(host) ? "github.com" : host;
    }

    /// <summary>The GitHub API host derived from the web host.</summary>
    public static string ApiHost(string? configuredHost)
    {
        var host = Normalize(configuredHost);
        if (string.IsNullOrEmpty(host) || host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "api.github.com";
        }

        return host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? host : $"api.{host}";
    }

    private static string Normalize(string? configuredHost)
    {
        if (string.IsNullOrWhiteSpace(configuredHost))
        {
            return string.Empty;
        }

        var host = configuredHost.Trim();
        host = host.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            host = host[..slash];
        }

        return host.Trim().TrimEnd('.');
    }
}
