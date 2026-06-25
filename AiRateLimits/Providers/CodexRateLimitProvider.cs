using AiRateLimits.Models;

namespace AiRateLimits.Providers;

/// <summary>
/// Codex provider. Primary implemented provider.
/// TODO: live path (GET https://chatgpt.com/backend-api/wham/usage), SQLite fallback
/// (~/.codex/logs_2.sqlite), local cost scanner, and account-credit parsing.
/// </summary>
public sealed class CodexRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "codex";

    public string Id => ProviderId;
    public string DisplayName => "Codex";

    public Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Scaffold stub: real implementation reads auth.json -> live API -> SQLite fallback.
        return Task.FromResult(VendorRateLimitSnapshot.Failed(
            Id, DisplayName, "Codex provider not yet implemented."));
    }
}
