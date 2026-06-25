using System.IO;

namespace AiRateLimits.Providers.JetBrains;

/// <summary>
/// Locates the JetBrains AI quota file. Honors a manual base path; otherwise scans known config
/// roots for Rider folders only and prefers the one whose quota file was written most recently.
/// </summary>
public static class JetBrainsDiscovery
{
    private const string QuotaRelativePath = @"options\AIAssistantQuotaManager2.xml";

    private static IEnumerable<string> ConfigRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(appData, "JetBrains");
        yield return Path.Combine(localAppData, "JetBrains");
        yield return Path.Combine(appData, "Google");
        yield return Path.Combine(localAppData, "Google");
    }

    /// <summary>
    /// Returns the quota file path to use, or null if none is found.
    /// </summary>
    public static string? FindQuotaFile(string? manualBasePath)
    {
        if (!string.IsNullOrWhiteSpace(manualBasePath))
        {
            var manual = Path.Combine(manualBasePath, QuotaRelativePath);
            return File.Exists(manual) ? manual : null;
        }

        var candidates = new List<(string Path, DateTime Written)>();

        foreach (var root in ConfigRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> ideDirs;
            try
            {
                ideDirs = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var ideDir in ideDirs)
            {
                // Automatic discovery is intentionally Rider-only; a manual path can target any IDE.
                var name = Path.GetFileName(ideDir);
                if (!name.StartsWith("Rider", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var quotaFile = Path.Combine(ideDir, QuotaRelativePath);
                if (File.Exists(quotaFile))
                {
                    candidates.Add((quotaFile, File.GetLastWriteTimeUtc(quotaFile)));
                }
            }
        }

        return candidates
            .OrderByDescending(c => c.Written)
            .Select(c => c.Path)
            .FirstOrDefault();
    }
}
