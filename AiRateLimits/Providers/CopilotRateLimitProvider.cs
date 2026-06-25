using AiRateLimits.Models;

namespace AiRateLimits.Providers;

/// <summary>
/// GitHub Copilot provider. Reads usage via GitHub device-flow OAuth plus the internal
/// copilot_internal/user endpoint. Token lives in Windows Credential Manager
/// (target: AiRateLimits.Copilot.GitHubOAuth).
/// TODO: device flow, token storage, usage API, premium/chat/top-up buckets.
/// </summary>
public sealed class CopilotRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "copilot";

    public string Id => ProviderId;
    public string DisplayName => "GitHub Copilot";

    public Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(VendorRateLimitSnapshot.Failed(
            Id, DisplayName, "GitHub Copilot provider not yet implemented.", source: "api headers"));
    }
}
