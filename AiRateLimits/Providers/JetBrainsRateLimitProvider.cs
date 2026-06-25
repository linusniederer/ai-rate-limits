using AiRateLimits.Models;

namespace AiRateLimits.Providers;

/// <summary>
/// JetBrains AI provider. Reads local IDE quota state from
/// &lt;IDE base path&gt;\options\AIAssistantQuotaManager2.xml (Rider folders by default).
/// TODO: config-root discovery, XML/embedded-JSON parsing, tariff + top-up buckets.
/// </summary>
public sealed class JetBrainsRateLimitProvider : IRateLimitProvider
{
    public const string ProviderId = "jetbrains";

    public string Id => ProviderId;
    public string DisplayName => "JetBrains AI";

    public Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(VendorRateLimitSnapshot.Failed(
            Id, DisplayName, "JetBrains AI provider not yet implemented.", source: "manual"));
    }
}
