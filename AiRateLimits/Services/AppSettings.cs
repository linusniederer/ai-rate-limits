using AiRateLimits.Providers;

namespace AiRateLimits.Services;

/// <summary>
/// Persisted user settings. Autostart and provider tokens are intentionally NOT stored here
/// (autostart is read from the OS registry; tokens live in the OS credential vault).
/// </summary>
public sealed class AppSettings
{
    public int WarningUsedPercent { get; set; } = 85;
    public int CriticalUsedPercent { get; set; } = 100;
    public int RefreshMinutes { get; set; } = 5;

    public List<string> EnabledVendors { get; set; } = new() { CodexRateLimitProvider.ProviderId };
    public string StatusVendorId { get; set; } = CodexRateLimitProvider.ProviderId;

    public decimal? CodexCreditPriceUsd { get; set; }
    public decimal? CodexCreditMultiplier { get; set; }
    public decimal? CodexTopUpCreditTotal { get; set; }

    public string JetBrainsIdeBasePath { get; set; } = string.Empty;
    public string CopilotEnterpriseHost { get; set; } = string.Empty;

    /// <summary>Clamps all values to their valid ranges in-place. Mirrors blueprint validation rules.</summary>
    public void Normalize()
    {
        WarningUsedPercent = Math.Clamp(WarningUsedPercent, 1, 100);
        CriticalUsedPercent = Math.Clamp(CriticalUsedPercent, WarningUsedPercent, 100);
        RefreshMinutes = Math.Clamp(RefreshMinutes, 1, 240);

        CodexCreditPriceUsd = AsPositiveOrNull(CodexCreditPriceUsd);
        CodexCreditMultiplier = AsPositiveOrNull(CodexCreditMultiplier);
        CodexTopUpCreditTotal = AsPositiveOrNull(CodexTopUpCreditTotal);

        EnabledVendors ??= new List<string>();
        StatusVendorId = string.IsNullOrWhiteSpace(StatusVendorId)
            ? CodexRateLimitProvider.ProviderId
            : StatusVendorId;
    }

    /// <summary>Effective credit multiplier: unset/zero/negative behaves as 1.</summary>
    public decimal EffectiveCodexCreditMultiplier => CodexCreditMultiplier is { } m && m > 0 ? m : 1m;

    private static decimal? AsPositiveOrNull(decimal? value) =>
        value is { } v && v > 0 ? v : null;
}
