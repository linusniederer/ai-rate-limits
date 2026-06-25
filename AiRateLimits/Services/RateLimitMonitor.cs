using AiRateLimits.Models;
using AiRateLimits.Providers;
using Serilog;

namespace AiRateLimits.Services;

/// <summary>
/// Owns the providers, polls them, and exposes the latest snapshots plus computed health.
/// Scaffold version implements normal + unknown-state polling and a non-overlapping refresh.
/// TODO: reset-aware extra polling and notification dispatch.
/// </summary>
public sealed class RateLimitMonitor : IDisposable
{
    private static readonly TimeSpan UnknownRetryInterval = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IRateLimitProvider> _providers;
    private readonly SettingsStore _settingsStore;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, VendorRateLimitSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private System.Threading.Timer? _timer;
    private AppSettings _settings;

    public RateLimitMonitor(IEnumerable<IRateLimitProvider> providers, SettingsStore settingsStore)
    {
        _providers = providers.ToList();
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
    }

    /// <summary>Raised after each refresh completes with the current snapshot set.</summary>
    public event Action<IReadOnlyDictionary<string, VendorRateLimitSnapshot>>? Updated;

    public IReadOnlyDictionary<string, VendorRateLimitSnapshot> Snapshots => _snapshots;

    public AppSettings Settings => _settings;

    public void ReloadSettings() => _settings = _settingsStore.Load();

    public void Start()
    {
        _timer = new System.Threading.Timer(async _ => await RefreshAsync().ConfigureAwait(false));
        ScheduleNext(TimeSpan.Zero);
    }

    /// <summary>Forces a refresh now. Ignored (returns false) if one is already running.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            foreach (var provider in _providers)
            {
                if (!_settings.EnabledVendors.Contains(provider.Id, StringComparer.OrdinalIgnoreCase))
                {
                    _snapshots.Remove(provider.Id);
                    continue;
                }

                try
                {
                    _snapshots[provider.Id] = await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Provider {Provider} failed during refresh", provider.Id);
                    _snapshots[provider.Id] = VendorRateLimitSnapshot.Failed(
                        provider.Id, provider.DisplayName, ex.Message);
                }
            }

            Updated?.Invoke(_snapshots);
            ScheduleNext(NextInterval());
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public HealthCalculator.HealthResult StatusProviderHealth()
    {
        _snapshots.TryGetValue(_settings.StatusVendorId, out var snapshot);
        return HealthCalculator.ForStatusProvider(
            snapshot, _settings.WarningUsedPercent, _settings.CriticalUsedPercent);
    }

    public HealthCalculator.HealthResult AggregateHealth() =>
        HealthCalculator.Aggregate(
            _snapshots.Values, _settings.WarningUsedPercent, _settings.CriticalUsedPercent);

    private TimeSpan NextInterval() =>
        StatusProviderHealth().Health == LimitHealth.Unknown
            ? UnknownRetryInterval
            : TimeSpan.FromMinutes(_settings.RefreshMinutes);

    private void ScheduleNext(TimeSpan delay) =>
        _timer?.Change(delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}
