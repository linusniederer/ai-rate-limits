using AiRateLimits.Models;

namespace AiRateLimits.Providers;

/// <summary>
/// Contract every provider implements. Implementations must catch their own errors and
/// return a failed snapshot, obey cancellation, and never decide tray color.
/// </summary>
public interface IRateLimitProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<VendorRateLimitSnapshot> ReadAsync(CancellationToken cancellationToken);
}
