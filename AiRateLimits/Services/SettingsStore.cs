using System.IO;
using System.Text.Json;

namespace AiRateLimits.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\AiRateLimits\settings.json.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiRateLimits",
            "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults rather than crashing.
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
