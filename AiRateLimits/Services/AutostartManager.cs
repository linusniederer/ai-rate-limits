using Microsoft.Win32;

namespace AiRateLimits.Services;

/// <summary>
/// Manages the HKCU Run-key autostart entry. The autostart state is read directly from the
/// registry, never persisted in settings.json.
/// </summary>
public static class AutostartManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiRateLimits-8F2D4D4F";
    private const string MinimizedArg = "--minimized";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private static string DesiredCommand => $"\"{ExecutablePath}\" {MinimizedArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(ValueName, DesiredCommand);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// If autostart is enabled but the stored command is missing the --minimized flag (older
    /// entry), rewrite it to the current command. No-op otherwise.
    /// </summary>
    public static void MigrateIfNeeded()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not string current || string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        if (!current.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(ValueName, DesiredCommand);
        }
    }
}
