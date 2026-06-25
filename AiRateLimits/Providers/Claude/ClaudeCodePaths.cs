using System.IO;

namespace AiRateLimits.Providers.Claude;

/// <summary>
/// Shared location of the rate-limit snapshot written by the Claude Code statusline helper.
/// The helper script (tools/claude-statusline-capture.ps1) must write to this same path.
/// </summary>
public static class ClaudeCodePaths
{
    /// <summary>%LOCALAPPDATA%\AiRateLimits\claude\rate_limits.json</summary>
    public static string RateLimitsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiRateLimits", "claude", "rate_limits.json");
}
